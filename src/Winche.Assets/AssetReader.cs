using Winche.Rules;
using Winche.Rules.Evaluation;
using Winche.Storage.Locations;
using Winche.Storage.Models;
using Winche.Values;

namespace Winche.Assets;

/// <summary>A channel's release as a caller may see it: only the files the rules let them list.</summary>
public sealed record AssetListing(string Channel, int Release, DateTimeOffset PublishedAt, IReadOnlyDictionary<string, AssetFile> Files);

/// <summary>How a caller gets a file's bytes: a signed link to the location, or a stream from the server.</summary>
public sealed record AssetDownload(AssetFile File, string? Url, Func<CancellationToken, Task<Stream?>>? Open, DateTimeOffset? ExpiresAt = null);

/// <summary>
/// What apps read, through the assets rules (default deny): a channel's listing (files they may <c>list</c>) and files
/// (that they may <c>get</c>). Rules see the file as <c>resource</c> (path, size, mimeType, hash, release, channel), the
/// path's captures, and <c>request.auth</c>.
/// </summary>
public sealed class AssetReader(AssetStore store, AssetArchives archives, RuleEngine rules, Func<IReadOnlyDictionary<string, object?>?> claims, AssetSettings settings,
    AssetCdnSigner cdns)
{
    public async Task<AssetListing> ListingAsync(string channel, CancellationToken ct = default)
    {
        var c = await store.ChannelAsync(channel, ct) ?? throw new AssetsException("not-found", $"There's no channel {channel}.");
        var release = await store.ReleaseAsync(c.Release, ct) ?? throw new AssetsException("not-found", $"Release {c.Release} is gone.");
        var visible = new Dictionary<string, AssetFile>(StringComparer.Ordinal);
        foreach (var (path, file) in release.Files)
            if (await AllowsAsync(RuleOperation.List, path, file, release.Number, channel, ct)) visible[path] = file;
        return new AssetListing(channel, release.Number, release.At, visible);
    }

    /// <summary>A file of a channel's current release.</summary>
    public async Task<AssetDownload> ChannelFileAsync(string channel, string path, CancellationToken ct = default)
    {
        var c = await store.ChannelAsync(channel, ct) ?? throw new AssetsException("not-found", $"There's no channel {channel}.");
        return await FileAsync(c.Release, path, channel, ct);
    }

    /// <summary>A file of a given release (the SDK pins the release it syncs).</summary>
    public async Task<AssetDownload> FileAsync(int release, string path, string? channel = null, CancellationToken ct = default)
    {
        var r = await store.ReleaseAsync(release, ct) ?? throw new AssetsException("not-found", $"There's no release {release}.");
        if (!r.Files.TryGetValue(path, out var file)) throw new AssetsException("not-found", $"There's no {path} in release {release}.");
        if (!await AllowsAsync(RuleOperation.Get, path, file, release, channel, ct)) throw new AssetsException("permission-denied", $"You may not read {path}.");
        if (file.Url is { } url)
        {
            // On a CDN: a link it accepts for a few minutes (its address alone gets nothing).
            var (signed, expires) = await cdns.SignAsync(url, ct);
            return new AssetDownload(file, signed, null, expires);
        }
        var archive = await archives.ActiveAsync(ct);
        var key = AssetPaths.ContentKey(file.Hash);
        if (!settings.StreamFiles)
        {
            try
            {
                var link = await archive.GenerateDownloadUrlAsync(key, new DownloadHeaders(file.MimeType, null, "private, max-age=31536000, immutable"), ct);
                return new AssetDownload(file, link.Url, null, link.ExpiresAt);
            }
            catch (NotSupportedException) { /* stream it instead */ }
        }
        if (archive is not IArchiveContent bytes) throw new AssetsException("failed-precondition", "This location can't hand bytes to the server.");
        return new AssetDownload(file, null, c => bytes.OpenReadAsync(key, c));
    }

    private Task<bool> AllowsAsync(RuleOperation operation, string path, AssetFile file, int release, string? channel, CancellationToken ct) =>
        rules.AllowsAsync(operation, path, new RuleRequest
        {
            Resource = new MapValue(new Dictionary<string, Value>
            {
                ["path"] = new StringValue(path), ["size"] = new IntegerValue(file.Size), ["mimeType"] = new StringValue(file.MimeType),
                ["hash"] = new StringValue(file.Hash), ["release"] = new IntegerValue(release),
                ["channel"] = channel is null ? RuleValues.Null : new StringValue(channel),
            }),
            Request = RuleValues.Request(claims(), operation == RuleOperation.List ? "list" : "get", RuleValues.Null),
        }, ct);
}
