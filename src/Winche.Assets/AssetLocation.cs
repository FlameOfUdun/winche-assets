using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Winche.Core;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;

namespace Winche.Assets;

/// <summary>
/// The settings area <c>assets.location</c>: where contents are kept (null: the archive in the app's code), and a move
/// under way. A move copies every content to the new place, then switches; until then reads use the old place and
/// new contents go to both.
/// </summary>
public sealed record AssetLocationSettings
{
    public const string Area = "assets.location";

    public ArchiveLocation? Active { get; init; }
    public AssetMove? Move { get; init; }

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (Active is { } a) p.AddRange(a.Problems());
        if (Move is { } m) p.AddRange(m.To.Problems());
        return p;
    }
}

/// <summary>A move to another location: how far the copy got, and why it stopped if it did.</summary>
public sealed record AssetMove(ArchiveLocation To, int Copied = 0, int Total = 0, string? Error = null, DateTimeOffset? StartedAt = null);

/// <summary>The archive(s) contents go to and come from, as the location settings say.</summary>
public sealed class AssetArchives(SettingsArea<AssetLocationSettings>? area, ArchiveLocations locations)
{
    private AssetLocationSettings State => area?.Current ?? new AssetLocationSettings();

    /// <summary>Where contents are read from (the old place, during a move).</summary>
    public Task<IArchive> ActiveAsync(CancellationToken ct) => locations.GetAsync(State.Active, ct);

    /// <summary>Where new contents go: the active place, and the target of a move under way.</summary>
    public async Task<IReadOnlyList<IArchive>> WriteTargetsAsync(CancellationToken ct)
    {
        var list = new List<IArchive> { await ActiveAsync(ct) };
        if (State.Move is { Error: null } m) list.Add(await locations.GetAsync(m.To.Kind == "code" ? null : m.To, ct));
        return list;
    }

    public string Describe() => State.Active?.Describe() ?? "the app's own location";

    internal SettingsArea<AssetLocationSettings>? Area => area;
    internal ArchiveLocations Locations => locations;
}

/// <summary>Starting and cancelling a move (the console's guided steps); the worker carries it out.</summary>
public sealed class AssetMoves(AssetArchives archives, IServiceProvider services)
{
    private SettingsArea<AssetLocationSettings> Area => archives.Area
        ?? throw new AssetsException("failed-precondition", "The location is set in code here (AddWincheAssets without UseSettings).");
    private SettingsStore Store => services.GetService<SettingsStore>()
        ?? throw new AssetsException("failed-precondition", "Saving settings needs AddWincheConfig.");

    public AssetLocationSettings State => Area.Current;

    /// <summary>
    /// Starts copying every content to <paramref name="to"/> (its secret key kept in the project's secrets); it becomes
    /// the location once all are there. "code" moves back to the app's own location.
    /// </summary>
    public async Task StartAsync(ArchiveLocation to, string? secretKey, string? by, string reason, CancellationToken ct)
    {
        if (State.Move is { Error: null }) throw new AssetsException("failed-precondition", "A move is under way: wait for it, or cancel it.");
        if (to.Problems() is { Count: > 0 } p) throw new AssetsException("invalid-argument", "That location isn't complete.", p);
        if ((to.Kind == "code" && State.Active is null) || (to.Kind != "code" && to.Id == State.Active?.Id))
            throw new AssetsException("invalid-argument", "Assets are already kept there.");
        if (to.Kind != "code")
        {
            archives.Locations.Build(to, secretKey);   // a kind this app can build, or it throws now
            if (!string.IsNullOrEmpty(secretKey))
            {
                var secrets = services.GetService<SecretStore>() ?? throw new AssetsException("failed-precondition", "Keeping a secret key needs AddWincheConfig with a master key.");
                await secrets.SetAsync(to.SecretName, secretKey, by, ct);
            }
        }
        await SaveAsync(State with { Move = new AssetMove(to, StartedAt: DateTimeOffset.UtcNow) }, by, reason, ct);
    }

    /// <summary>Whether <paramref name="location"/> works for assets: write, read back, make a link, delete.</summary>
    public async Task<IReadOnlyList<(string Step, string? Error)>> TestAsync(ArchiveLocation location, string? secretKey, CancellationToken ct)
    {
        var steps = new List<(string, string?)>();
        if (location.Problems() is { Count: > 0 } p) return [("Settings", string.Join(" ", p))];
        IArchive archive;
        try { archive = location.Kind == "code" || secretKey is null ? await archives.Locations.GetAsync(location.Kind == "code" ? null : location, ct) : archives.Locations.Build(location, secretKey); }
        catch (Exception ex) { return [("Connect", ex.GetBaseException().Message)]; }
        if (archive is not IArchiveContent content) return [("Write", "The server can't write into this kind of location.")];
        var key = $"winche-assets/test/{Guid.NewGuid():N}";
        async Task<bool> Step(string name, Func<Task> body)
        {
            try { await body(); steps.Add((name, null)); return true; }
            catch (Exception ex) when (!ct.IsCancellationRequested) { steps.Add((name, ex.GetBaseException().Message)); return false; }
        }
        _ = await Step("Write", () => content.WriteAsync(key, new MemoryStream("test"u8.ToArray()), "text/plain", ct))
            && await Step("Read back", async () =>
            {
                await using var s = await content.OpenReadAsync(key, ct) ?? throw new InvalidOperationException("It wasn't there.");
                using var r = new StreamReader(s);
                if (await r.ReadToEndAsync(ct) != "test") throw new InvalidOperationException("It came back different.");
            });
        await Step("Delete", () => archive.DeleteAsync(key, ct));
        return steps;
    }

    public async Task CancelAsync(string? by, CancellationToken ct)
    {
        if (State.Move is null) throw new AssetsException("failed-precondition", "No move is under way.");
        await SaveAsync(State with { Move = null }, by, "Move cancelled", ct);
    }

    internal Task SaveAsync(AssetLocationSettings next, string? by, string reason, CancellationToken ct) =>
        Store.SaveAsync(AssetLocationSettings.Area, JsonSerializer.SerializeToNode(next, SettingsArea.Json)!, by, reason, ct);
}

/// <summary>Carries out a move: copies what's missing at the target, then switches the location.</summary>
internal sealed class AssetMoveWorker(AssetArchives archives, AssetMoves moves, AssetStore store, ILogger<AssetMoveWorker> logger) : BackgroundService
{
    internal static TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try { await RunOnceAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Moving assets failed; trying again shortly."); }
            try { await Task.Delay(Tick, stop); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        if (archives.Area is not { } area || area.Current.Move is not { Error: null } move) return;
        var from = await archives.ActiveAsync(ct);
        var to = await archives.Locations.GetAsync(move.To.Kind == "code" ? null : move.To, ct);
        if (from is not IArchiveContent source || to is not IArchiveContent target)
        {
            await moves.SaveAsync(area.Current with { Move = move with { Error = "One of the locations can't be read or written by the server, so contents can't be moved." } }, null, "Move stopped", ct);
            return;
        }
        var contents = await store.ContentsAsync(ct);
        var copied = 0;
        foreach (var (hash, _) in contents)
        {
            var key = AssetPaths.ContentKey(hash);
            if (await to.HeadAsync(key, ct) is null)
            {
                await using var stream = await source.OpenReadAsync(key, ct)
                    ?? throw new InvalidOperationException($"Content {hash} isn't at the current location.");
                await target.WriteAsync(key, stream, "application/octet-stream", ct);
            }
            if (++copied % 100 == 0) await moves.SaveAsync(area.Current with { Move = move with { Copied = copied, Total = contents.Count } }, null, "Moving", ct);
        }
        // Everything is there: the target becomes the location.
        await moves.SaveAsync(new AssetLocationSettings { Active = move.To.Kind == "code" ? null : move.To, Move = null }, null, $"Assets moved to {move.To.Describe()}", ct);
        logger.LogInformation("Assets moved to {Location} ({Count} contents).", move.To.Describe(), contents.Count);
    }
}
