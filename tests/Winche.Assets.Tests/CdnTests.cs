using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Winche.Assets.Tests;

[Collection("postgres")]
public sealed class CdnTests(PostgresFixture pg)
{
    private const string Video = "https://cdn.example.com/app/procedures/bvm.mp4";

    private static HttpRequestMessage Get(string url, string? uid = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, url);
        if (uid is not null) m.Headers.Add("X-Uid", uid);
        return m;
    }

    [Fact]
    public async Task A_cdn_file_is_hashed_once_published_by_address_and_read_through_signed_links_only()
    {
        var cdn = new FakeCdn();
        cdn.Put(Video, "the video", "\"v1\"");
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync(), cdn: cdn);
        var assets = app.Service<WincheAssets>();

        // The server reads it through a signed link, once per version (ETag).
        var seen = (await assets.InspectCdnFilesAsync([Video])).Single();
        Assert.Null(seen.Error);
        Assert.Equal(AssetPaths.Hash("the video"u8.ToArray()), seen.Hash);
        Assert.Equal(9, seen.Size);
        await assets.InspectCdnFilesAsync([Video]);
        Assert.Equal(1, cdn.Gets);

        // Published by address beside a stored file; nothing is uploaded for it.
        var stored = await app.PublishAsync(new() { ["translations/en.json"] = "{}" });
        var release = await assets.PublishAsync(new AssetPublishRequest(new Dictionary<string, AssetFile>
        {
            ["translations/en.json"] = stored.Files["translations/en.json"],
            ["videos/bvm.mp4"] = new(seen.Hash!, seen.Size, "", Video),
        }, "videos on the CDN", "production"), "admin@x");
        Assert.Equal("video/mp4", release.Files["videos/bvm.mp4"].MimeType);

        // Apps see path, hash and size: never the address.
        var listing = await app.Http.SendAsync(Get("/assets/v1/channels/production", uid: "ann"));
        var text = await listing.Content.ReadAsStringAsync();
        Assert.Contains("videos/bvm.mp4", text);
        Assert.DoesNotContain("cdn.example.com", text);

        // Reading it: the rules first (videos need a signed-in user), then a link the CDN accepts for a few minutes.
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(Get("/assets/v1/channels/production/files/videos/bvm.mp4?link=true"))).StatusCode);
        var link = await (await app.Http.SendAsync(Get("/assets/v1/channels/production/files/videos/bvm.mp4?link=true", uid: "ann")))
            .Content.ReadFromJsonAsync<JsonElement>();
        var signed = new Uri(link.GetProperty("url").GetString()!);
        Assert.Equal(seen.Hash, link.GetProperty("hash").GetString());
        var q = System.Web.HttpUtility.ParseQueryString(signed.Query);
        Assert.True(cdn.Verifies(Video, q["Expires"], q["Signature"], q["Key-Pair-Id"]));
        Assert.Equal("the video", await new HttpClient(cdn).GetStringAsync(signed));
        cdn.Now = DateTimeOffset.UtcNow.AddMinutes(11);   // past the link's ten minutes
        Assert.Equal(HttpStatusCode.Forbidden, (await new HttpClient(cdn).GetAsync(signed)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await new HttpClient(cdn).GetAsync(Video)).StatusCode);   // the address alone: nothing
        cdn.Now = DateTimeOffset.UtcNow;

        // Without ?link: a redirect to such a link. With the server's rights (functions): the bytes.
        var redirect = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/videos/bvm.mp4", uid: "ann"));
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.StartsWith(Video + "?Expires=", redirect.Headers.Location!.ToString());
        var opened = (await assets.OpenAsync(release.Number, "videos/bvm.mp4"))!.Value;
        using (var reader = new StreamReader(opened.Content)) Assert.Equal("the video", await reader.ReadToEndAsync());
        Assert.StartsWith(Video + "?Expires=", (await assets.DownloadUrlAsync(release.Number, "videos/bvm.mp4"))!.Url);
    }

    [Fact]
    public async Task Only_files_the_server_read_on_a_known_cdn_are_published()
    {
        var cdn = new FakeCdn();
        cdn.Put(Video, "the video", "\"v1\"");
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync(), cdn: cdn);
        var assets = app.Service<WincheAssets>();
        var hash = AssetPaths.Hash("the video"u8.ToArray());

        var notRead = await Assert.ThrowsAsync<AssetsException>(() => assets.PublishAsync(new AssetPublishRequest(
            new Dictionary<string, AssetFile> { ["videos/bvm.mp4"] = new(hash, 9, "", Video) }, "x", "production"), "admin@x"));
        Assert.Contains(notRead.Problems, p => p.Contains("hasn't been read"));
        var elsewhere = await Assert.ThrowsAsync<AssetsException>(() => assets.PublishAsync(new AssetPublishRequest(
            new Dictionary<string, AssetFile> { ["videos/x.mp4"] = new(hash, 9, "", "https://other.example.com/x.mp4") }, "x", "production"), "admin@x"));
        Assert.Contains(elsewhere.Problems, p => p.Contains("no CDN set up"));

        // Unknown address, missing file: said per file, not thrown.
        cdn.Put("https://cdn.example.com/app/gone.mp4", "", "\"g\"");
        cdn.Files.TryRemove("https://cdn.example.com/app/gone.mp4", out _);
        var answers = await assets.InspectCdnFilesAsync(["https://cdn.example.com/app/gone.mp4", "https://other.example.com/x.mp4"]);
        Assert.All(answers, a => Assert.NotNull(a.Error));

        // A file changed on the CDN (new ETag) is read again; the old hash stays valid for releases that have it.
        await assets.InspectCdnFilesAsync([Video]);
        cdn.Put(Video, "a new cut", "\"v2\"");
        var again = (await assets.InspectCdnFilesAsync([Video])).Single();
        Assert.Equal(AssetPaths.Hash("a new cut"u8.ToArray()), again.Hash);
        Assert.Equal(2, cdn.Gets);
    }
}
