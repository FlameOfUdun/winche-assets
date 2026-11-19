using System.Text.Json.Serialization;

namespace Winche.Assets;

/// <summary>
/// A file of a release: its content's SHA-256 (lowercase hex), size and type. <see cref="Url"/> is set for a file kept
/// on a CDN instead of here: apps never see it, they get a short-lived signed link.
/// </summary>
public sealed record AssetFile(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("mimeType")] string MimeType,
    [property: JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Url = null);

/// <summary>A published release: numbered, never changed.</summary>
public sealed record AssetRelease(int Number, string? Note, string? By, DateTimeOffset At, IReadOnlyDictionary<string, AssetFile> Files)
{
    public long TotalSize => Files.Values.Sum(f => f.Size);
}

/// <summary>A release without its files (for listings).</summary>
public sealed record AssetReleaseSummary(int Number, string? Note, string? By, DateTimeOffset At, int FileCount, long TotalSize);

/// <summary>A named pointer to a release that apps read (production, beta, …).</summary>
public sealed record AssetChannel(string Name, int Release, DateTimeOffset UpdatedAt, string? By);

/// <summary>A channel's move, for its history.</summary>
public sealed record AssetChannelMove(string Channel, int? From, int To, DateTimeOffset At, string? By);

/// <summary>How two releases differ, by path.</summary>
public sealed record AssetReleaseDiff(IReadOnlyList<string> Added, IReadOnlyList<string> Changed, IReadOnlyList<string> Removed);

/// <summary>What publishing takes: the files by path (contents sent beforehand), a note, and optionally a channel to move.</summary>
public sealed record AssetPublishRequest(IReadOnlyDictionary<string, AssetFile> Files, string? Note, string? Channel = null);

/// <summary>A refusal with one of the shared error codes (invalid-argument, not-found, failed-precondition, …).</summary>
public sealed class AssetsException(string code, string message, IReadOnlyList<string>? problems = null) : Exception(message)
{
    public string Code { get; } = code;
    public IReadOnlyList<string> Problems { get; } = problems ?? [];
}
