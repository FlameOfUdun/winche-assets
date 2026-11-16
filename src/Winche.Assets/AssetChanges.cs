using Microsoft.Extensions.DependencyInjection;
using Winche;

namespace Winche.Assets;

/// <summary>Releases and channel moves, for webhooks and functions (source <c>assets</c>).</summary>
public static class AssetChanges
{
    public static readonly WincheChangeSource Source = new("assets", "Assets", ["released", "channelMoved"], HasPaths: false,
        Variables: ["release", "channel"]);

    internal static Task ReleasedAsync(IServiceProvider services, AssetRelease release, string? channel, CancellationToken ct) =>
        PublishAsync(services, "released", new System.Text.Json.Nodes.JsonObject
        {
            ["release"] = release.Number, ["note"] = release.Note, ["by"] = release.By, ["files"] = release.Files.Count, ["channel"] = channel,
        }, new() { ["release"] = release.Number.ToString(), ["channel"] = channel ?? "" }, ct);

    internal static Task ChannelMovedAsync(IServiceProvider services, string channel, int release, CancellationToken ct) =>
        PublishAsync(services, "channelMoved", new System.Text.Json.Nodes.JsonObject { ["channel"] = channel, ["release"] = release },
            new() { ["release"] = release.ToString(), ["channel"] = channel }, ct);

    private static Task PublishAsync(IServiceProvider services, string type, System.Text.Json.Nodes.JsonObject data, Dictionary<string, string> vars, CancellationToken ct) =>
        WincheChanges.PublishAsync(services.GetServices<IWincheChangeSink>(), new WincheChange("assets", type, null, DateTimeOffset.UtcNow, data, null, vars), ct);
}
