using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Winche.Assets;

/// <summary>
/// Keeps the newest releases (KeepReleases) and every one a channel points at; then removes contents no release uses
/// (left an hour, so one sent for a release being published isn't taken).
/// </summary>
public sealed class AssetSweep(AssetStore store, AssetArchives archives, AssetSettings settings, TimeProvider time, ILogger<AssetSweep> logger)
{
    public static readonly TimeSpan Grace = TimeSpan.FromHours(1);

    public async Task<(int Releases, int Contents)> RunAsync(CancellationToken ct = default)
    {
        var releases = settings.Limits.KeepReleases > 0 ? await store.TrimReleasesAsync(settings.Limits.KeepReleases, ct) : 0;
        var unused = await store.UnusedContentsAsync(time.GetUtcNow() - Grace, ct);
        foreach (var hash in unused)
        {
            foreach (var archive in await archives.WriteTargetsAsync(ct)) await archive.DeleteAsync(AssetPaths.ContentKey(hash), ct);
            await store.RemoveContentAsync(hash, ct);
        }
        if (releases > 0 || unused.Count > 0) logger.LogInformation("Asset sweep: {Releases} old releases and {Contents} unused contents removed.", releases, unused.Count);
        return (releases, unused.Count);
    }
}

internal sealed class AssetSweepService(AssetSweep sweep, ILogger<AssetSweepService> logger) : BackgroundService
{
    internal static TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stop); } catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            try { await sweep.RunAsync(stop); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "The asset sweep failed; it runs again later."); }
            try { await Task.Delay(Every, stop); } catch (OperationCanceledException) { return; }
        }
    }
}
