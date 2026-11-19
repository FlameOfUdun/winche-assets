using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;
using Winche.Secrets;

namespace Winche.Assets;

/// <summary>
/// The settings area <c>assets.cdns</c>: CDNs whose files a release may name instead of storing them. Apps never see
/// their addresses: each read is checked by the rules, then answered with a link the CDN accepts for a few minutes.
/// </summary>
public sealed record AssetCdnSettings
{
    public const string Area = "assets.cdns";

    public IReadOnlyList<AssetCdn> Cdns { get; init; } = [];

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        foreach (var cdn in Cdns) p.AddRange(cdn.Problems().Select(x => $"{(cdn.Id is { Length: > 0 } id ? id : "A CDN")}: {x}"));
        foreach (var dup in Cdns.GroupBy(c => c.Id, StringComparer.Ordinal).Where(g => g.Count() > 1)) p.Add($"There are two CDNs named {dup.Key}.");
        return p;
    }
}

/// <summary>
/// A CDN releases may name files on. <c>cloudfront</c>: apps get links it accepts for a few minutes (signed URLs with a
/// canned policy; its private key is the project secret <see cref="SecretName"/>, a value or a reference such as
/// <c>aws-sm:…</c>). <c>public</c>: a CDN that serves its files to anyone; apps get the address itself.
/// </summary>
public sealed record AssetCdn
{
    /// <summary>Its name: names its secret (assets.cdn.{id}.privateKey).</summary>
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "cloudfront";
    /// <summary>The files' addresses start with it, e.g. <c>https://cdn.example.com/app/</c>.</summary>
    public string BaseUrl { get; init; } = "";
    /// <summary>CloudFront's public key id (in the key group the distribution trusts).</summary>
    public string KeyPairId { get; init; } = "";
    /// <summary>How long a link works (default 10 minutes).</summary>
    public int LinkMinutes { get; init; } = 10;

    public string SecretName => $"assets.cdn.{Id}.privateKey";

    /// <summary>Whether its links are signed (else apps get the file's address as it is).</summary>
    public bool Signs => Kind == "cloudfront";

    /// <summary>Whether <paramref name="url"/> is one of this CDN's files.</summary>
    public bool Serves(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && Uri.TryCreate(BaseUrl, UriKind.Absolute, out var b)
        && u.Scheme == b.Scheme && string.Equals(u.Authority, b.Authority, StringComparison.OrdinalIgnoreCase)
        && u.AbsolutePath.StartsWith(b.AbsolutePath, StringComparison.Ordinal) && string.IsNullOrEmpty(u.Query) && string.IsNullOrEmpty(u.Fragment);

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (string.IsNullOrWhiteSpace(Id) || Id.Length > 40 || Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) p.Add("Name it with letters, digits and -.");
        if (Kind is not ("cloudfront" or "public")) p.Add("A CDN is \"cloudfront\" (signed links) or \"public\" (its addresses as they are).");
        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var b) || b.Scheme != "https" || !string.IsNullOrEmpty(b.Query)) p.Add("The address is https://, with no query.");
        if (Kind == "cloudfront" && string.IsNullOrWhiteSpace(KeyPairId)) p.Add("Give the key pair (public key) id.");
        if (LinkMinutes is < 1 or > 60) p.Add("A link works 1 to 60 minutes.");
        return p;
    }
}

/// <summary>A CDN file looked at: its content's hash and size (or why it can't be used).</summary>
public sealed record AssetCdnFile(string Url, string? Hash, long Size, string? MimeType, string? Error = null);

/// <summary>The HTTP client CDN files are read with (tests give their own handler).</summary>
public sealed class AssetCdnHttp(HttpClient http)
{
    public HttpClient Client => http;
}

/// <summary>Signs CDN links and reads CDN files with the server's rights.</summary>
public sealed class AssetCdnSigner(SettingsArea<AssetCdnSettings>? area, IReadOnlyList<(AssetCdn Cdn, string? PrivateKey)> code,
    IServiceProvider services, TimeProvider time)
{
    private readonly ConcurrentDictionary<string, (string Stamp, RSA Key)> _keys = new(StringComparer.Ordinal);

    /// <summary>The CDNs in force: the console's, or the code's when the settings aren't used.</summary>
    public IReadOnlyList<AssetCdn> Cdns => area?.Current.Cdns is { Count: > 0 } set ? set : [.. code.Select(c => c.Cdn)];

    /// <summary>The CDN serving <paramref name="url"/>, if any.</summary>
    public AssetCdn? For(string url) => Cdns.FirstOrDefault(c => c.Serves(url));

    /// <summary>A link to <paramref name="url"/> that works for the CDN's link minutes.</summary>
    public async Task<(string Url, DateTimeOffset ExpiresAt)> SignAsync(string url, CancellationToken ct)
    {
        var cdn = For(url) ?? throw new AssetsException("failed-precondition", "No CDN set up in Assets settings serves this file.");
        if (!cdn.Signs) return (url, time.GetUtcNow().AddDays(1));   // a public CDN: its address, for as long as a download takes
        var key = await KeyAsync(cdn, ct);
        var expires = time.GetUtcNow().AddMinutes(cdn.LinkMinutes);
        return (CloudFront.Sign(url, cdn.KeyPairId, key, expires), expires);
    }

    /// <summary>The file at <paramref name="url"/>, through a signed link, with the server's rights.</summary>
    public async Task<HttpResponseMessage> GetAsync(string url, HttpMethod method, CancellationToken ct)
    {
        var (signed, _) = await SignAsync(url, ct);
        var http = services.GetRequiredService<AssetCdnHttp>().Client;
        return await http.SendAsync(new HttpRequestMessage(method, signed), HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>A PEM key as kept: line breaks put back when it lost them (pasted on one line, or kept so in AWS).</summary>
    public static string Pem(string text)
    {
        text = text.Trim();
        if (text.Contains('\n')) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"^(-----BEGIN [A-Z ]+-----)\s*(.*?)\s*(-----END [A-Z ]+-----)$", m =>
            $"{m.Groups[1].Value}\n{System.Text.RegularExpressions.Regex.Replace(m.Groups[2].Value, @"\s+", "\n")}\n{m.Groups[3].Value}");
    }

    private async Task<RSA> KeyAsync(AssetCdn cdn, CancellationToken ct)
    {
        var pem = code.FirstOrDefault(c => c.Cdn.Id == cdn.Id && c.PrivateKey is not null).PrivateKey;
        if (pem is null && area is not null)
        {
            var secrets = services.GetService<SecretStore>();
            try { pem = secrets is null ? null : await secrets.GetAsync(cdn.SecretName, ct); }
            catch (SecretReferenceException ex) { throw new AssetsException("unavailable", $"{cdn.Id}'s private key can't be read: {ex.Message}"); }
        }
        if (string.IsNullOrWhiteSpace(pem)) throw new AssetsException("failed-precondition", $"{cdn.Id} has no private key (Assets settings → CDNs).");
        var stamp = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(pem)));
        if (_keys.TryGetValue(cdn.Id, out var hit) && hit.Stamp == stamp) return hit.Key;
        var rsa = RSA.Create();
        try { rsa.ImportFromPem(Pem(pem)); }
        catch (ArgumentException) { rsa.Dispose(); throw new AssetsException("failed-precondition", $"{cdn.Id}'s private key isn't an RSA key in PEM."); }
        _keys[cdn.Id] = (stamp, rsa);
        return rsa;
    }
}

/// <summary>CloudFront signed URLs with a canned policy (an expiry only).</summary>
public static class CloudFront
{
    public static string Sign(string url, string keyPairId, RSA key, DateTimeOffset expires)
    {
        var epoch = expires.ToUnixTimeSeconds();
        var policy = "{\"Statement\":[{\"Resource\":\"" + url + "\",\"Condition\":{\"DateLessThan\":{\"AWS:EpochTime\":" + epoch + "}}}]}";
        var signature = key.SignData(Encoding.UTF8.GetBytes(policy), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
        var safe = Convert.ToBase64String(signature).Replace('+', '-').Replace('=', '_').Replace('/', '~');
        return $"{url}{(url.Contains('?') ? '&' : '?')}Expires={epoch}&Signature={safe}&Key-Pair-Id={Uri.EscapeDataString(keyPairId)}";
    }
}

/// <summary>
/// CDN files' hashes, read once per version of the file (its ETag, or size and date without one): a deploy names
/// files by address, and the server hashes each new one through a signed link.
/// </summary>
public sealed class AssetCdnFiles(AssetStore store, AssetCdnSigner signer, TimeProvider time)
{
    public async Task<IReadOnlyList<AssetCdnFile>> InspectAsync(IReadOnlyList<string> urls, CancellationToken ct)
    {
        var list = new List<AssetCdnFile>();
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            try { list.Add(await InspectAsync(url, ct)); }
            catch (AssetsException ex) { list.Add(new AssetCdnFile(url, null, 0, null, ex.Message)); }
            catch (HttpRequestException ex) { list.Add(new AssetCdnFile(url, null, 0, null, $"The CDN can't be reached ({ex.Message}).")); }
        }
        return list;
    }

    private async Task<AssetCdnFile> InspectAsync(string url, CancellationToken ct)
    {
        if (signer.For(url) is null) throw new AssetsException("failed-precondition", "No CDN set up in Assets settings serves this address.");
        using (var head = await signer.GetAsync(url, HttpMethod.Head, ct))
        {
            if (!head.IsSuccessStatusCode) throw new AssetsException("not-found", $"The CDN answered {(int)head.StatusCode} for it (is the key pair trusted?).");
            var version = Version(head);
            if (version is not null && await store.CdnFileAsync(url, version, ct) is { } known) return known;
        }
        using var get = await signer.GetAsync(url, HttpMethod.Get, ct);
        if (!get.IsSuccessStatusCode) throw new AssetsException("not-found", $"The CDN answered {(int)get.StatusCode} for it.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using (var body = await get.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0) { sha.AppendData(buffer, 0, read); size += read; }
        }
        var file = new AssetCdnFile(url, Convert.ToHexStringLower(sha.GetHashAndReset()), size, get.Content.Headers.ContentType?.MediaType);
        await store.AddCdnFileAsync(file, Version(get) ?? $"read-{time.GetUtcNow():O}", time.GetUtcNow(), ct);
        return file;
    }

    /// <summary>What says the file changed: its ETag, or its size and date.</summary>
    private static string? Version(HttpResponseMessage r) =>
        r.Headers.ETag?.Tag is { Length: > 0 } tag ? tag
        : r.Content.Headers.ContentLength is { } length && r.Content.Headers.LastModified is { } modified ? $"{length}@{modified:O}"
        : null;

    /// <summary>Whether <paramref name="hash"/> was read from <paramref name="url"/> (publishing takes only those).</summary>
    public Task<bool> KnownAsync(string url, string hash, CancellationToken ct) => store.CdnFileKnownAsync(url, hash, ct);
}
