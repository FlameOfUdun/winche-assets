using System.Net;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;
using Winche.Assets.AspNetCore;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;
using Winche.Storage.Models;

namespace Winche.Assets.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"a{Guid.NewGuid():N}"[..20];
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

/// <summary>An archive in memory: bytes by key, and download links (mem://key) unless it's told it has none.</summary>
public sealed class MemoryArchive(bool links = false) : IArchive, IArchiveContent
{
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public async Task WriteAsync(string path, Stream content, string? contentType, CancellationToken ct = default)
    {
        using var copy = new MemoryStream();
        await content.CopyToAsync(copy, ct);
        Objects[path] = copy.ToArray();
    }
    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<Stream?>(Objects.TryGetValue(path, out var b) ? new MemoryStream(b) : null);
    public Task<DownloadSession> GenerateDownloadUrlAsync(string path, DownloadHeaders? headers = null, CancellationToken ct = default) =>
        links ? Task.FromResult(new DownloadSession { Url = $"mem://{path}?type={headers?.ContentType}", ExpiresAt = DateTime.UtcNow.AddMinutes(5) })
              : throw new NotSupportedException("no links");
    public Task<ArchiveObjectInfo?> HeadAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<ArchiveObjectInfo?>(Objects.TryGetValue(path, out var b) ? new ArchiveObjectInfo(b.Length, null) : null);
    public Task DeleteAsync(string path, CancellationToken ct = default) { Objects.TryRemove(path, out _); return Task.CompletedTask; }
    public Task DeleteObjectsAsync(IEnumerable<string> paths, CancellationToken ct = default) { foreach (var p in paths) Objects.TryRemove(p, out _); return Task.CompletedTask; }
    public async IAsyncEnumerable<ArchivedObject> ListObjectsAsync(string? prefix = null, [EnumeratorCancellation] CancellationToken ct = default) { await Task.CompletedTask; yield break; }
    public Task<UploadSession> GenerateUploadUrlAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> CreateMultipartUploadAsync(string path, string mimeType, long sizeBytes, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<UploadSession> SignPartAsync(string path, string uploadId, int partNumber, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string?> CompleteMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IEnumerable<FilePart>> ListPartsAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task AbortMultipartUploadAsync(string path, string uploadId, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Builds "s3" locations as the same second in-memory archive (so a move has somewhere to go).</summary>
public sealed class MemoryLocations(MemoryArchive target) : IArchiveProvider
{
    public string Kind => "s3";
    public IArchive Create(ArchiveLocation location, string? secretKey) => target;
}

/// <summary>A clock tests move.</summary>
public sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Changes published, captured.</summary>
public sealed class CapturedChanges : Winche.IWincheChangeSink
{
    public List<Winche.WincheChange> All { get; } = [];
    public Task OnChangesAsync(IReadOnlyList<Winche.WincheChange> changes, CancellationToken ct) { lock (All) All.AddRange(changes); return Task.CompletedTask; }
}

/// <summary>A test host: Winche Assets with an in-memory archive, its endpoints, and a caller from the X-Uid header.</summary>
public sealed class AssetsApp : IAsyncDisposable
{
    private readonly WebApplication _app;
    public HttpClient Http { get; }
    public MemoryArchive Archive { get; }
    public MemoryArchive Target { get; }
    public TestClock Clock { get; }
    public CapturedChanges Changes { get; }
    public IServiceProvider Services => _app.Services;
    public T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private AssetsApp(WebApplication app, MemoryArchive archive, MemoryArchive target, TestClock clock, CapturedChanges changes)
    {
        _app = app;
        Archive = archive;
        Target = target;
        Clock = clock;
        Changes = changes;
        Http = app.GetTestClient();
    }

    public const string Rules = """
        match translations/{rest=**} { allow read; }
        match videos/{rest=**} { allow read: if request.auth.uid != null; }
        match staff/{rest=**} { allow get: if request.auth.token.role == 'staff'; }
        """;

    public static async Task<AssetsApp> StartAsync(string db, bool links = false, bool settings = false, AssetLimits? limits = null,
        FakeCdn? cdn = null)
    {
        var archive = new MemoryArchive(links);
        var b = WebApplication.CreateBuilder();
        b.WebHost.UseTestServer();
        var clock = new TestClock();
        var changes = new CapturedChanges();
        var target = new MemoryArchive();
        b.Services.AddSingleton<TimeProvider>(clock);
        b.Services.AddSingleton<Winche.IWincheChangeSink>(changes);
        b.Services.AddSingleton<IArchiveProvider>(new MemoryLocations(target));
        if (settings) Winche.Core.ServiceCollectionExtensions.AddWincheConfig(b.Services, o => o.ConnectionString = db);
        b.Services.AddWincheAssets(o =>
        {
            o.ConnectionString = db;
            o.UseArchive(_ => archive);
            o.UseRulesText(Rules);
            if (limits is not null) o.Limits = limits;
            if (settings) o.UseSettings();
            if (cdn is not null) o.UseCdn(cdn.Cdn, cdn.PrivateKeyPem);
        });
        if (cdn is not null) b.Services.AddSingleton(new AssetCdnHttp(new HttpClient(cdn)));
        var app = b.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers["X-Uid"].ToString() is { Length: > 0 } uid)
                Winche.WincheCaller.Set(new Dictionary<string, object?> { ["uid"] = uid, ["role"] = ctx.Request.Headers["X-Role"].ToString() });
            await next();
        });
        app.MapWincheAssets();
        await app.StartAsync();
        await app.Services.GetRequiredService<AssetStore>().InitializeAsync();
        return new AssetsApp(app, archive, target, clock, changes);
    }

    /// <summary>Sends contents and publishes a release of them.</summary>
    public async Task<AssetRelease> PublishAsync(Dictionary<string, string> files, string? channel = "production", string? note = null)
    {
        var assets = Service<WincheAssets>();
        var map = new Dictionary<string, AssetFile>();
        foreach (var (path, text) in files)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            var hash = AssetPaths.Hash(bytes);
            if ((await assets.MissingAsync([hash])).Count > 0) await assets.PutContentAsync(new MemoryStream(bytes), hash);
            map[path] = new AssetFile(hash, bytes.Length, "");
        }
        return await assets.PublishAsync(new AssetPublishRequest(map, note, channel), "admin@x");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>
/// A CloudFront stand-in: files by address, served only to requests whose canned-policy signature checks out with the
/// key pair's public key and hasn't expired (else 403, as CloudFront does). Counts the GETs it answers.
/// </summary>
public sealed class FakeCdn : HttpMessageHandler
{
    private readonly System.Security.Cryptography.RSA _key = System.Security.Cryptography.RSA.Create(2048);
    public ConcurrentDictionary<string, (byte[] Bytes, string ETag)> Files { get; } = new(StringComparer.Ordinal);
    public int Gets;
    public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
    public AssetCdn Cdn { get; } = new() { Id = "media", BaseUrl = "https://cdn.example.com/app/", KeyPairId = "K2TESTKEY" };
    public string PrivateKeyPem => _key.ExportPkcs8PrivateKeyPem();

    public void Put(string url, string text, string etag) => Files[url] = (System.Text.Encoding.UTF8.GetBytes(text), etag);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        var url = uri.GetLeftPart(UriPartial.Path);
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        if (!Verifies(url, q["Expires"], q["Signature"], q["Key-Pair-Id"])) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
        if (!Files.TryGetValue(url, out var f)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.Method == HttpMethod.Head ? [] : f.Bytes) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp4");
        r.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(f.ETag);
        if (request.Method == HttpMethod.Get) Interlocked.Increment(ref Gets);
        return Task.FromResult(r);
    }

    /// <summary>Whether a link is one this CDN accepts now.</summary>
    public bool Verifies(string url, string? expires, string? signature, string? keyPairId)
    {
        if (keyPairId != Cdn.KeyPairId || !long.TryParse(expires, out var epoch) || signature is null) return false;
        if (DateTimeOffset.FromUnixTimeSeconds(epoch) <= Now) return false;
        var policy = "{\"Statement\":[{\"Resource\":\"" + url + "\",\"Condition\":{\"DateLessThan\":{\"AWS:EpochTime\":" + epoch + "}}}]}";
        var bytes = Convert.FromBase64String(signature.Replace('-', '+').Replace('_', '=').Replace('~', '/'));
        return _key.VerifyData(System.Text.Encoding.UTF8.GetBytes(policy), bytes, System.Security.Cryptography.HashAlgorithmName.SHA1,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
    }
}
