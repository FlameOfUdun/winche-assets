using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Winche;

namespace Winche.Assets.AspNetCore;

/// <summary>
/// What apps read assets through (<c>/assets/v1</c>), with the caller the host set (UseWincheCaller):
/// <list type="bullet">
/// <item><c>GET channels/{channel}</c>: the channel's release and the files the caller may list (ETag; 304 when unchanged).</item>
/// <item><c>GET channels/{channel}/files/{**path}</c>: a file of the channel's current release.</item>
/// <item><c>GET releases/{n}/files/{**path}</c>: a file of a given release (never changes: cached for good).</item>
/// </list>
/// A file is a redirect to a short-lived signed link to the location, or (StreamFiles, or a location without links)
/// its bytes, with its hash as ETag. With <c>?link=true</c> it's that link as JSON instead (<c>{url, expiresAt, hash,
/// size, mimeType}</c>, url null when the bytes come from here), for clients that download it without their credentials.
/// </summary>
public static class AssetsEndpoints
{
    public static IEndpointRouteBuilder MapWincheAssets(this IEndpointRouteBuilder app, string prefix = "assets/v1")
    {
        var group = app.MapGroup(prefix);
        string[] methods = ["GET", "HEAD"];

        group.MapMethods("/channels/{channel}", methods, (string channel, HttpContext http, AssetReader reader, CancellationToken ct) =>
            Run(http, async () =>
            {
                var listing = await reader.ListingAsync(channel, ct);
                var etag = $"\"{listing.Release}-{Fingerprint(listing.Files.Keys)}\"";
                http.Response.Headers.CacheControl = "private, no-cache";
                http.Response.Headers.ETag = etag;
                if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
                return Results.Json(new
                {
                    channel = listing.Channel, release = listing.Release, publishedAt = listing.PublishedAt,
                    files = listing.Files.OrderBy(f => f.Key, StringComparer.Ordinal)
                        .Select(f => new { path = f.Key, hash = f.Value.Hash, size = f.Value.Size, mimeType = f.Value.MimeType }),
                });
            }));

        group.MapMethods("/channels/{channel}/files/{**path}", methods, (string channel, string path, HttpContext http, AssetReader reader, CancellationToken ct) =>
            Run(http, async () => await SendAsync(http, await reader.ChannelFileAsync(channel, path, ct), immutable: false, ct)));

        group.MapMethods("/releases/{release:int}/files/{**path}", methods, (int release, string path, HttpContext http, AssetReader reader, CancellationToken ct) =>
            Run(http, async () => await SendAsync(http, await reader.FileAsync(release, path, null, ct), immutable: true, ct)));

        return app;
    }

    private static async Task<IResult> SendAsync(HttpContext http, AssetDownload d, bool immutable, CancellationToken ct)
    {
        var etag = $"\"{d.File.Hash}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = immutable ? "private, max-age=31536000, immutable" : "private, no-cache";
        if (http.Request.Query["link"] == "true")
        {
            http.Response.Headers.CacheControl = "no-store";   // the link expires; the answer mustn't outlive it
            return Results.Json(new { url = d.Url, expiresAt = d.ExpiresAt, hash = d.File.Hash, size = d.File.Size, mimeType = d.File.MimeType });
        }
        if (http.Request.Headers.IfNoneMatch.ToString() == etag) return Results.StatusCode(StatusCodes.Status304NotModified);
        if (d.Url is { } url) return Results.Redirect(url);
        if (HttpMethods.IsHead(http.Request.Method))
        {
            http.Response.ContentLength = d.File.Size;
            http.Response.ContentType = d.File.MimeType;
            return Results.Empty;
        }
        var stream = await d.Open!(ct);
        if (stream is null) return new WincheError(WincheErrorCode.Unavailable, "The file's content is missing from the location.").ToResult();
        return Results.Stream(stream, d.File.MimeType, enableRangeProcessing: false);
    }

    /// <summary>What the caller may see changes the listing as much as the release does.</summary>
    private static string Fingerprint(IEnumerable<string> paths) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', paths.Order(StringComparer.Ordinal)))))[..16];

    private static async Task<IResult> Run(HttpContext http, Func<Task<IResult>> body)
    {
        try { return await body(); }
        catch (AssetsException ex)
        {
            return (WincheError.IsCode(ex.Code) ? new WincheError(WincheError.Parse(ex.Code), ex.Message) : new WincheError(WincheErrorCode.Internal, ex.Message)).ToResult();
        }
    }
}
