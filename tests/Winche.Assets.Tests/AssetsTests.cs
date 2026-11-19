using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Winche.Storage.Locations;

namespace Winche.Assets.Tests;

[Collection("postgres")]
public sealed class AssetsTests(PostgresFixture pg)
{
    private static HttpRequestMessage Get(string url, string? uid = null, string? role = null, string? etag = null)
    {
        var m = new HttpRequestMessage(HttpMethod.Get, url);
        if (uid is not null) m.Headers.Add("X-Uid", uid);
        if (role is not null) m.Headers.Add("X-Role", role);
        if (etag is not null) m.Headers.TryAddWithoutValidation("If-None-Match", etag);
        return m;
    }

    [Fact]
    public async Task Publishes_numbered_releases_and_moves_the_channel()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        var r1 = await app.PublishAsync(new() { ["translations/en.json"] = "{\"hi\":\"Hello\"}" }, note: "first");
        var r2 = await app.PublishAsync(new() { ["translations/en.json"] = "{\"hi\":\"Hi\"}", ["translations/de.json"] = "{}" });
        Assert.Equal(1, r1.Number);
        Assert.Equal(2, r2.Number);
        Assert.Equal("application/json", r2.Files["translations/de.json"].MimeType);
        var assets = app.Service<WincheAssets>();
        Assert.Equal(2, (await assets.ChannelsAsync()).Single().Release);
        var history = await assets.HistoryAsync("production");
        Assert.Equal([(int?)1, null], history.Select(h => h.From));
        var opened = (await assets.OpenAsync(2, "translations/de.json"))!.Value;   // the server's rights: no rules
        using (var reader = new StreamReader(opened.Content)) Assert.Equal("{}", await reader.ReadToEndAsync());
        Assert.Null(await assets.OpenAsync(1, "translations/de.json"));
        var diff = await assets.DiffAsync(1, 2);
        Assert.Equal(["translations/de.json"], diff.Added);
        Assert.Equal(["translations/en.json"], diff.Changed);
        Assert.Empty(diff.Removed);
        Assert.Equal(2, app.Changes.All.Count(c => c.Type == "released"));

        // Where a release was made: the console's are marked, a deploy's aren't.
        var fromConsole = await assets.PublishAsync(new AssetPublishRequest(r2.Files, "a fix", "production", "console"), "ann@x");
        Assert.Equal("console", (await assets.ReleaseAsync(fromConsole.Number))!.Source);
        Assert.Null((await assets.ReleaseAsync(2))!.Source);
        Assert.Equal("console", (await assets.ReleasesAsync()).First().Source);
        await Assert.ThrowsAsync<AssetsException>(() => assets.PublishAsync(new AssetPublishRequest(r2.Files, "x", null, "elsewhere"), "ann@x"));
    }

    [Fact]
    public async Task Refuses_bad_releases()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        var assets = app.Service<WincheAssets>();
        var unsent = new string('a', 64);
        var e = await Assert.ThrowsAsync<AssetsException>(() => assets.PublishAsync(new AssetPublishRequest(new Dictionary<string, AssetFile> { ["a.txt"] = new(unsent, 1, "") }, null), null));
        Assert.Equal("failed-precondition", e.Code);
        Assert.Equal(["a.txt"], e.Problems);
        e = await Assert.ThrowsAsync<AssetsException>(() => assets.PublishAsync(new AssetPublishRequest(new Dictionary<string, AssetFile> { ["../a"] = new(unsent, 1, "") }, null, "Bad Name"), null));
        Assert.Equal("invalid-argument", e.Code);
        Assert.Equal(2, e.Problems.Count);
        e = await Assert.ThrowsAsync<AssetsException>(() => assets.PutContentAsync(new MemoryStream("abc"u8.ToArray()), unsent));
        Assert.Contains("hash", e.Message);
        Assert.Empty(app.Archive.Objects);
        e = await Assert.ThrowsAsync<AssetsException>(() => assets.MoveChannelAsync("beta", 9, null));
        Assert.Equal("not-found", e.Code);
    }

    [Fact]
    public async Task Contents_are_stored_once()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.PublishAsync(new() { ["translations/a.json"] = "same", ["translations/b.json"] = "same" });
        await app.PublishAsync(new() { ["translations/c.json"] = "same" });
        Assert.Single(app.Archive.Objects);
        Assert.StartsWith("winche-assets/", app.Archive.Objects.Keys.Single());
    }

    [Fact]
    public async Task Listing_shows_what_rules_let_the_caller_list()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.PublishAsync(new() { ["translations/en.json"] = "{}", ["videos/intro.mp4"] = "v", ["staff/guide.html"] = "<p>", ["secret.txt"] = "s" });

        async Task<string[]> Paths(string? uid, string? role = null)
        {
            var res = await app.Http.SendAsync(Get("/assets/v1/channels/production", uid, role));
            res.EnsureSuccessStatusCode();
            var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, doc.GetProperty("release").GetInt32());
            return [.. doc.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("path").GetString()!)];
        }
        Assert.Equal(["translations/en.json"], await Paths(null));
        Assert.Equal(["translations/en.json", "videos/intro.mp4"], await Paths("u1"));
        Assert.Equal(["translations/en.json", "videos/intro.mp4"], await Paths("u1", "staff"));   // get only: not listed

        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.SendAsync(Get("/assets/v1/channels/beta"))).StatusCode);
    }

    [Fact]
    public async Task Listing_answers_304_until_the_channel_moves()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.PublishAsync(new() { ["translations/en.json"] = "1" });
        var first = await app.Http.SendAsync(Get("/assets/v1/channels/production"));
        var etag = first.Headers.ETag!.Tag;
        Assert.Equal(HttpStatusCode.NotModified, (await app.Http.SendAsync(Get("/assets/v1/channels/production", etag: etag))).StatusCode);
        // Signed in, the caller sees more: a different listing, a different tag.
        await app.PublishAsync(new() { ["videos/a.mp4"] = "v" }, channel: "other");
        await app.Service<WincheAssets>().MoveChannelAsync("production", 1, null);   // same release again: still 304
        Assert.Equal(HttpStatusCode.NotModified, (await app.Http.SendAsync(Get("/assets/v1/channels/production", etag: etag))).StatusCode);
        await app.PublishAsync(new() { ["translations/en.json"] = "2" });   // release 3
        Assert.Equal(HttpStatusCode.OK, (await app.Http.SendAsync(Get("/assets/v1/channels/production", etag: etag))).StatusCode);
    }

    [Fact]
    public async Task Files_stream_with_their_hash_and_rules()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        var r = await app.PublishAsync(new() { ["translations/en.json"] = "{\"a\":1}", ["staff/guide.html"] = "<p>staff</p>", ["videos/x.mp4"] = "vid" });

        var res = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("{\"a\":1}", await res.Content.ReadAsStringAsync());
        var streamed = await (await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json?link=true"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, streamed.GetProperty("url").ValueKind);   // no link: the bytes come from here
        Assert.Equal("application/json", res.Content.Headers.ContentType!.MediaType);
        Assert.Equal($"\"{r.Files["translations/en.json"].Hash}\"", res.Headers.ETag!.Tag);
        Assert.Equal(HttpStatusCode.NotModified, (await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json", etag: res.Headers.ETag.Tag))).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(Get("/assets/v1/releases/1/files/staff/guide.html", "u1"))).StatusCode);
        var staff = await app.Http.SendAsync(Get("/assets/v1/releases/1/files/staff/guide.html", "u1", "staff"));
        Assert.Equal("<p>staff</p>", await staff.Content.ReadAsStringAsync());
        Assert.Contains("immutable", staff.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Http.SendAsync(Get("/assets/v1/releases/1/files/videos/x.mp4"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.SendAsync(Get("/assets/v1/releases/1/files/nope.txt"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.SendAsync(Get("/assets/v1/releases/7/files/translations/en.json"))).StatusCode);
    }

    [Fact]
    public async Task Files_redirect_to_signed_links_when_the_location_has_them()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync(), links: true);
        var r = await app.PublishAsync(new() { ["translations/en.json"] = "{}" });
        var res = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json"));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith($"mem://{AssetPaths.ContentKey(r.Files["translations/en.json"].Hash)}", res.Headers.Location!.ToString());

        // Asked for the link itself (clients that download without their credentials): JSON, never cached.
        var link = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json?link=true"));
        Assert.Equal(HttpStatusCode.OK, link.StatusCode);
        var body = await link.Content.ReadFromJsonAsync<JsonElement>();
        Assert.StartsWith("mem://", body.GetProperty("url").GetString());
        Assert.Equal(r.Files["translations/en.json"].Hash, body.GetProperty("hash").GetString());
        Assert.Equal("no-store", link.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Rolling_back_is_moving_the_channel()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync());
        await app.PublishAsync(new() { ["translations/en.json"] = "old" });
        await app.PublishAsync(new() { ["translations/en.json"] = "new" });
        await app.Service<WincheAssets>().MoveChannelAsync("production", 1, "admin@x");
        var res = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/en.json"));
        Assert.Equal("old", await res.Content.ReadAsStringAsync());
        Assert.Contains(app.Changes.All, c => c.Type == "channelMoved");
        await app.Service<WincheAssets>().RemoveChannelAsync("production");
        Assert.Equal(HttpStatusCode.NotFound, (await app.Http.SendAsync(Get("/assets/v1/channels/production"))).StatusCode);
    }

    [Fact]
    public async Task Sweep_keeps_recent_and_pointed_at_releases_and_their_contents()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync(), limits: new AssetLimits { KeepReleases = 2 });
        await app.PublishAsync(new() { ["translations/a.json"] = "one" }, channel: "pinned");      // 1, a channel keeps it
        await app.PublishAsync(new() { ["translations/a.json"] = "two" }, channel: null);          // 2, goes
        await app.PublishAsync(new() { ["translations/a.json"] = "three" });                       // 3
        await app.PublishAsync(new() { ["translations/a.json"] = "four" });                        // 4
        Assert.Equal(4, app.Archive.Objects.Count);

        app.Clock.Now += TimeSpan.FromMinutes(5);
        Assert.Equal((1, 0), await app.Service<AssetSweep>().RunAsync());   // contents are too new yet
        app.Clock.Now += AssetSweep.Grace;
        Assert.Equal((0, 1), await app.Service<AssetSweep>().RunAsync());
        Assert.Equal([1, 3, 4], (await app.Service<WincheAssets>().ReleasesAsync()).Select(r => r.Number).Order());
        Assert.Equal(3, app.Archive.Objects.Count);
        Assert.DoesNotContain(AssetPaths.ContentKey(AssetPaths.Hash("two"u8)), app.Archive.Objects.Keys);
    }

    [Fact]
    public async Task Moves_contents_to_another_location_then_switches()
    {
        await using var app = await AssetsApp.StartAsync(await pg.NewDatabaseAsync(), settings: true);
        await app.PublishAsync(new() { ["translations/en.json"] = "en", ["translations/de.json"] = "de" });
        var moves = app.Service<AssetMoves>();
        await moves.StartAsync(new ArchiveLocation { Id = "elsewhere", Kind = "s3", S3 = new S3LocationSettings { Bucket = "b", Region = "eu-west-2" } }, null, "admin@x", "Moving", default);
        Assert.All(await moves.TestAsync(moves.State.Move!.To, null, default), s => Assert.Null(s.Error));
        await WaitAsync(() => moves.State.Move is not null);

        // During the move, new contents go to both places.
        await app.PublishAsync(new() { ["translations/fr.json"] = "fr" });
        Assert.Single(app.Target.Objects);

        var worker = ActivatorUtilities.CreateInstance<AssetMoveWorker>(app.Services, NullLogger<AssetMoveWorker>.Instance);
        await worker.RunOnceAsync(default);
        await WaitAsync(() => moves.State.Move is null);
        Assert.Equal("elsewhere", moves.State.Active?.Id);
        Assert.Equal(3, app.Target.Objects.Count);

        app.Archive.Objects.Clear();   // reads come from the new place now
        var res = await app.Http.SendAsync(Get("/assets/v1/releases/1/files/translations/de.json"));
        Assert.Equal("de", await res.Content.ReadAsStringAsync());
        res = await app.Http.SendAsync(Get("/assets/v1/channels/production/files/translations/fr.json"));
        Assert.Equal("fr", await res.Content.ReadAsStringAsync());
    }

    private static async Task WaitAsync(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(50);
        Assert.True(done());
    }
}
