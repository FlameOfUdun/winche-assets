using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Storage.Locations;

namespace Winche.Assets;

/// <summary>
/// Publishing and managing assets, with the server's rights (no rules): contents in, releases out, channels moved.
/// Used by the console API and CI deploys; apps read through <see cref="AssetReader"/>.
/// </summary>
public sealed class WincheAssets(AssetStore store, AssetArchives archives, AssetSettings settings, IServiceProvider services, TimeProvider time)
{
    public AssetLimits Limits => settings.Limits;

    /// <summary>Which of these contents the location doesn't have yet.</summary>
    public Task<IReadOnlyList<string>> MissingAsync(IReadOnlyCollection<string> hashes, CancellationToken ct = default)
    {
        if (hashes.Any(h => !AssetPaths.IsHash(h))) throw new AssetsException("invalid-argument", "Hashes are SHA-256 in lowercase hex.");
        return store.MissingAsync(hashes, ct);
    }

    /// <summary>
    /// Stores a content after checking it hashes to <paramref name="expectedHash"/> (it's read whole first, into a
    /// temporary file). Storing one that's there already changes nothing.
    /// </summary>
    public async Task PutContentAsync(Stream content, string expectedHash, CancellationToken ct = default)
    {
        if (!AssetPaths.IsHash(expectedHash)) throw new AssetsException("invalid-argument", "The hash is SHA-256 in lowercase hex.");
        var temp = Path.Combine(Path.GetTempPath(), $"winche-asset-{Guid.NewGuid():N}");
        try
        {
            long size = 0;
            using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            await using (var file = File.Create(temp))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await content.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > Limits.MaxFileBytes) throw new AssetsException("out-of-range", $"A file is at most {Limits.MaxFileBytes / 1024 / 1024} MB.");
                    sha.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                }
                if (Convert.ToHexStringLower(sha.GetHashAndReset()) != expectedHash)
                    throw new AssetsException("invalid-argument", "The content doesn't match its hash (sent incompletely?).");
            }
            foreach (var archive in await archives.WriteTargetsAsync(ct))
            {
                if (archive is not IArchiveContent bytes) throw new AssetsException("failed-precondition", "This location can't take bytes from the server.");
                await using var file = File.OpenRead(temp);
                await bytes.WriteAsync(AssetPaths.ContentKey(expectedHash), file, "application/octet-stream", ct);
            }
            await store.AddContentAsync(expectedHash, size, time.GetUtcNow(), ct);
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>Publishes a release (every content sent beforehand) and moves the channel to it when one is given.</summary>
    public async Task<AssetRelease> PublishAsync(AssetPublishRequest request, string? by, CancellationToken ct = default)
    {
        var problems = new List<string>();
        if (request.Files.Count == 0) problems.Add("A release has at least one file.");
        if (request.Files.Count > Limits.MaxFilesPerRelease) problems.Add($"A release has at most {Limits.MaxFilesPerRelease} files.");
        foreach (var (path, f) in request.Files)
        {
            if (AssetPaths.PathProblem(path) is { } bad) problems.Add($"{path}: {bad}.");
            if (!AssetPaths.IsHash(f.Hash)) problems.Add($"{path}: the hash is SHA-256 in lowercase hex.");
            if (f.Size < 0 || f.Size > Limits.MaxFileBytes) problems.Add($"{path} is larger than {Limits.MaxFileBytes / 1024 / 1024} MB.");
        }
        if (request.Channel is { } c && AssetPaths.ChannelProblem(c) is { } badChannel) problems.Add($"{c}: {badChannel}.");
        if (request.Note is { Length: > 500 }) problems.Add("A note is up to 500 characters.");
        if (problems.Count > 0) throw new AssetsException("invalid-argument", "This release can't be published.", problems.Take(50).ToList());
        if (await store.MissingAsync([.. request.Files.Values.Select(f => f.Hash)], ct) is { Count: > 0 } missing)
            throw new AssetsException("failed-precondition", "Some contents haven't been sent.",
                [.. request.Files.Where(f => missing.Contains(f.Value.Hash)).Select(f => f.Key).Take(50)]);
        var files = request.Files.ToDictionary(f => f.Key,
            f => f.Value with { MimeType = string.IsNullOrWhiteSpace(f.Value.MimeType) ? AssetPaths.MimeTypeOf(f.Key) : f.Value.MimeType }, StringComparer.Ordinal);
        var release = await store.AddReleaseAsync(files, request.Note?.Trim(), by, request.Channel, time.GetUtcNow(), ct);
        await AssetChanges.ReleasedAsync(services, release, request.Channel, ct);
        return release;
    }

    /// <summary>Points a channel at a release (made when it's new): a rollback is moving it to an older one.</summary>
    public async Task<AssetChannel> MoveChannelAsync(string channel, int release, string? by, CancellationToken ct = default)
    {
        if (AssetPaths.ChannelProblem(channel) is { } bad) throw new AssetsException("invalid-argument", $"{channel}: {bad}.");
        if (await store.ReleaseAsync(release, ct) is null) throw new AssetsException("not-found", $"There's no release {release}.");
        await store.MoveChannelAsync(channel, release, by, time.GetUtcNow(), ct);
        await AssetChanges.ChannelMovedAsync(services, channel, release, ct);
        return (await store.ChannelAsync(channel, ct))!;
    }

    public async Task RemoveChannelAsync(string channel, CancellationToken ct = default)
    {
        if (!await store.RemoveChannelAsync(channel, ct)) throw new AssetsException("not-found", $"There's no channel {channel}.");
    }

    public Task<IReadOnlyList<AssetChannel>> ChannelsAsync(CancellationToken ct = default) => store.ChannelsAsync(ct);
    public Task<IReadOnlyList<AssetChannelMove>> HistoryAsync(string channel, int limit = 50, CancellationToken ct = default) => store.HistoryAsync(channel, limit, ct);
    public Task<IReadOnlyList<AssetReleaseSummary>> ReleasesAsync(int limit = 100, CancellationToken ct = default) => store.ReleasesAsync(limit, ct);
    public Task<AssetRelease?> ReleaseAsync(int number, CancellationToken ct = default) => store.ReleaseAsync(number, ct);

    /// <summary>What changed from release <paramref name="from"/> to <paramref name="to"/>.</summary>
    public async Task<AssetReleaseDiff> DiffAsync(int from, int to, CancellationToken ct = default)
    {
        var a = await store.ReleaseAsync(from, ct) ?? throw new AssetsException("not-found", $"There's no release {from}.");
        var b = await store.ReleaseAsync(to, ct) ?? throw new AssetsException("not-found", $"There's no release {to}.");
        return new AssetReleaseDiff(
            [.. b.Files.Keys.Where(p => !a.Files.ContainsKey(p)).Order(StringComparer.Ordinal)],
            [.. b.Files.Where(f => a.Files.TryGetValue(f.Key, out var old) && old.Hash != f.Value.Hash).Select(f => f.Key).Order(StringComparer.Ordinal)],
            [.. a.Files.Keys.Where(p => !b.Files.ContainsKey(p)).Order(StringComparer.Ordinal)]);
    }

    /// <summary>Where contents are kept, in words.</summary>
    public string Location => archives.Describe();
}
