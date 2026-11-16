using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Winche.Assets;

/// <summary>Paths, names, hashes and types.</summary>
public static partial class AssetPaths
{
    /// <summary>Where a content is kept in the location: one object per SHA-256, whatever the releases using it.</summary>
    public static string ContentKey(string hash) => $"winche-assets/{hash[..2]}/{hash}";

    public static string Hash(ReadOnlySpan<byte> content) => Convert.ToHexStringLower(SHA256.HashData(content));

    public static bool IsHash(string? s) => s is { Length: 64 } && s.All(char.IsAsciiHexDigitLower);

    /// <summary>What's wrong with a file path, or null: relative, / between parts, no empty, . or .. parts.</summary>
    public static string? PathProblem(string path) =>
        path.Length is 0 or > 500 ? "a path is 1 to 500 characters"
        : path.StartsWith('/') || path.EndsWith('/') || path.Contains('\\') ? "a path is relative, with / between its parts"
        : path.Split('/').Any(p => p is "" or "." or ".." || p.Any(char.IsControl)) ? "a path has no empty, . or .. parts"
        : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,39}$")]
    private static partial Regex ChannelName();

    /// <summary>What's wrong with a channel name, or null: lowercase letters, digits, . _ -, up to 40.</summary>
    public static string? ChannelProblem(string name) =>
        ChannelName().IsMatch(name) ? null : "a channel's name is lowercase letters, digits, . _ - (up to 40, starting with a letter or digit)";

    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".json"] = "application/json", [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8", [".xml"] = "application/xml", [".csv"] = "text/csv; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif",
        [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon",
        [".mp4"] = "video/mp4", [".m4v"] = "video/mp4", [".mov"] = "video/quicktime", [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg", [".m4a"] = "audio/mp4", [".wav"] = "audio/wav", [".aac"] = "audio/aac", [".ogg"] = "audio/ogg",
        [".pdf"] = "application/pdf", [".zip"] = "application/zip",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".ttf"] = "font/ttf", [".otf"] = "font/otf",
        [".glb"] = "model/gltf-binary", [".gltf"] = "model/gltf+json",
    };

    /// <summary>The type for a path's extension; application/octet-stream when unknown.</summary>
    public static string MimeTypeOf(string path) => Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
}

/// <summary>Limits on releases (settings).</summary>
public sealed record AssetLimits
{
    /// <summary>The most files a release may hold.</summary>
    public int MaxFilesPerRelease { get; init; } = 10_000;
    /// <summary>The largest file, in bytes (512 MB).</summary>
    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;
    /// <summary>Releases kept: older ones no channel points at are deleted by the sweep (0 keeps all).</summary>
    public int KeepReleases { get; init; } = 50;

    public IReadOnlyList<string> Problems()
    {
        var p = new List<string>();
        if (MaxFilesPerRelease is < 1 or > 100_000) p.Add("A release holds 1 to 100,000 files.");
        if (MaxFileBytes is < 1 or > 5L * 1024 * 1024 * 1024) p.Add("The largest file is 1 byte to 5 GB.");
        if (KeepReleases is < 0 or > 10_000) p.Add("Keep 0 (all) to 10,000 releases.");
        return p;
    }
}
