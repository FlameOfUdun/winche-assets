using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using Winche.Core;

namespace Winche.Assets;

/// <summary>Releases, channels and the contents they use, in the project's database.</summary>
public sealed class AssetStore([FromKeyedServices(AssetStore.DataSourceKey)] NpgsqlDataSource db)
{
    public const string DataSourceKey = "winche-assets";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private bool _ready;

    internal static readonly string[] Steps =
    [
        """
            CREATE TABLE IF NOT EXISTS winche_asset_contents (
              hash text PRIMARY KEY,
              size bigint NOT NULL,
              added_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE TABLE IF NOT EXISTS winche_asset_releases (
              number int PRIMARY KEY,
              note text NULL,
              published_by text NULL,
              published_at timestamptz NOT NULL,
              files jsonb NOT NULL
            );
            CREATE TABLE IF NOT EXISTS winche_asset_channels (
              name text PRIMARY KEY,
              release int NOT NULL REFERENCES winche_asset_releases(number),
              updated_at timestamptz NOT NULL,
              updated_by text NULL
            );
            CREATE TABLE IF NOT EXISTS winche_asset_channel_moves (
              id bigserial PRIMARY KEY,
              channel text NOT NULL,
              from_release int NULL,
              to_release int NOT NULL,
              moved_at timestamptz NOT NULL,
              moved_by text NULL
            );
            CREATE INDEX IF NOT EXISTS winche_asset_channel_moves_channel ON winche_asset_channel_moves (channel, id DESC);
        """,
        """
            CREATE TABLE IF NOT EXISTS winche_asset_cdn_files (
              url text NOT NULL,
              version text NOT NULL,
              hash text NOT NULL,
              size bigint NOT NULL,
              mime_type text NULL,
              read_at timestamptz NOT NULL,
              PRIMARY KEY (url, version)
            );
            CREATE INDEX IF NOT EXISTS winche_asset_cdn_files_hash ON winche_asset_cdn_files (url, hash);
        """,
    ];

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return;
        await WincheSchema.EnsureAsync(db, "assets", Steps, ct);
        _ready = true;
    }

    // ── CDN files ───────────────────────────────────────────────────────────

    internal async Task<AssetCdnFile?> CdnFileAsync(string url, string version, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT hash, size, mime_type FROM winche_asset_cdn_files WHERE url = $1 AND version = $2");
        cmd.Parameters.AddWithValue(url);
        cmd.Parameters.AddWithValue(version);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new AssetCdnFile(url, r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2)) : null;
    }

    internal async Task AddCdnFileAsync(AssetCdnFile file, string version, DateTimeOffset at, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            INSERT INTO winche_asset_cdn_files (url, version, hash, size, mime_type, read_at) VALUES ($1, $2, $3, $4, $5, $6)
            ON CONFLICT (url, version) DO UPDATE SET hash = excluded.hash, size = excluded.size, mime_type = excluded.mime_type, read_at = excluded.read_at
            """);
        cmd.Parameters.AddWithValue(file.Url);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(file.Hash!);
        cmd.Parameters.AddWithValue(file.Size);
        cmd.Parameters.AddWithValue((object?)file.MimeType ?? DBNull.Value);
        cmd.Parameters.AddWithValue(at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    internal async Task<bool> CdnFileKnownAsync(string url, string hash, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT 1 FROM winche_asset_cdn_files WHERE url = $1 AND hash = $2 LIMIT 1");
        cmd.Parameters.AddWithValue(url);
        cmd.Parameters.AddWithValue(hash);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    // ── contents ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<string>> MissingAsync(IReadOnlyCollection<string> hashes, CancellationToken ct)
    {
        await InitializeAsync(ct);
        var distinct = hashes.Distinct().ToArray();
        if (distinct.Length == 0) return [];
        await using var cmd = db.CreateCommand("SELECT hash FROM winche_asset_contents WHERE hash = ANY($1)");
        cmd.Parameters.AddWithValue(distinct);
        var found = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) found.Add(r.GetString(0));
        return [.. distinct.Where(h => !found.Contains(h))];
    }

    internal async Task AddContentAsync(string hash, long size, DateTimeOffset at, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("INSERT INTO winche_asset_contents (hash, size, added_at) VALUES ($1, $2, $3) ON CONFLICT (hash) DO NOTHING");
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(size);
        cmd.Parameters.AddWithValue(at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every content hash recorded (for moving them to another location).</summary>
    internal async Task<IReadOnlyList<(string Hash, long Size)>> ContentsAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT hash, size FROM winche_asset_contents ORDER BY hash");
        var list = new List<(string, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add((r.GetString(0), r.GetInt64(1)));
        return list;
    }

    /// <summary>Contents no release uses, recorded before <paramref name="before"/> (so one sent for a release being published isn't taken).</summary>
    internal async Task<IReadOnlyList<string>> UnusedContentsAsync(DateTimeOffset before, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            SELECT c.hash FROM winche_asset_contents c
            WHERE c.added_at < $1
              AND NOT EXISTS (SELECT 1 FROM winche_asset_releases r, jsonb_each(r.files) f WHERE f.value->>'hash' = c.hash)
            """);
        cmd.Parameters.AddWithValue(before);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(r.GetString(0));
        return list;
    }

    internal async Task RemoveContentAsync(string hash, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("DELETE FROM winche_asset_contents WHERE hash = $1");
        cmd.Parameters.AddWithValue(hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── releases ────────────────────────────────────────────────────────────

    /// <summary>Adds the next release (and moves <paramref name="channel"/> to it), in one transaction.</summary>
    internal async Task<AssetRelease> AddReleaseAsync(IReadOnlyDictionary<string, AssetFile> files, string? note, string? by, string? channel, DateTimeOffset at, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await using (var lockCmd = new NpgsqlCommand("LOCK TABLE winche_asset_releases IN SHARE ROW EXCLUSIVE MODE", conn, tx)) await lockCmd.ExecuteNonQueryAsync(ct);
        int number;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO winche_asset_releases (number, note, published_by, published_at, files)
            SELECT coalesce(max(number), 0) + 1, $1, $2, $3, $4 FROM winche_asset_releases RETURNING number
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue((object?)note ?? DBNull.Value);
            cmd.Parameters.AddWithValue((object?)by ?? DBNull.Value);
            cmd.Parameters.AddWithValue(at);
            cmd.Parameters.AddWithValue(NpgsqlDbType.Jsonb, JsonSerializer.Serialize(files, Json));
            number = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (channel is not null) await MoveAsync(conn, tx, channel, number, by, at, ct);
        await tx.CommitAsync(ct);
        return new AssetRelease(number, note, by, at, files);
    }

    public async Task<AssetRelease?> ReleaseAsync(int number, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT number, note, published_by, published_at, files FROM winche_asset_releases WHERE number = $1");
        cmd.Parameters.AddWithValue(number);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new AssetRelease(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
            JsonSerializer.Deserialize<Dictionary<string, AssetFile>>(r.GetString(4), Json)!);
    }

    /// <summary>Releases, newest first, without their files.</summary>
    public async Task<IReadOnlyList<AssetReleaseSummary>> ReleasesAsync(int limit, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            SELECT number, note, published_by, published_at,
                   (SELECT count(*) FROM jsonb_each(files)), (SELECT coalesce(sum((value->>'size')::bigint), 0) FROM jsonb_each(files))
            FROM winche_asset_releases ORDER BY number DESC LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limit);
        var list = new List<AssetReleaseSummary>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            list.Add(new(r.GetInt32(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetFieldValue<DateTimeOffset>(3),
                (int)r.GetInt64(4), Convert.ToInt64(r.GetValue(5))));
        return list;
    }

    /// <summary>Deletes releases past the newest <paramref name="keep"/> that no channel points at; how many went.</summary>
    internal async Task<int> TrimReleasesAsync(int keep, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("""
            DELETE FROM winche_asset_releases r
            WHERE r.number <= (SELECT coalesce(max(number), 0) FROM winche_asset_releases) - $1
              AND NOT EXISTS (SELECT 1 FROM winche_asset_channels c WHERE c.release = r.number)
            """);
        cmd.Parameters.AddWithValue(keep);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── channels ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AssetChannel>> ChannelsAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT name, release, updated_at, updated_by FROM winche_asset_channels ORDER BY name");
        var list = new List<AssetChannel>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new(r.GetString(0), r.GetInt32(1), r.GetFieldValue<DateTimeOffset>(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public async Task<AssetChannel?> ChannelAsync(string name, CancellationToken ct) =>
        (await ChannelsAsync(ct)).FirstOrDefault(c => c.Name == name);

    internal async Task MoveChannelAsync(string channel, int release, string? by, DateTimeOffset at, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await MoveAsync(conn, tx, channel, release, by, at, ct);
        await tx.CommitAsync(ct);
    }

    private static async Task MoveAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string channel, int release, string? by, DateTimeOffset at, CancellationToken ct)
    {
        int? from;
        await using (var get = new NpgsqlCommand("SELECT release FROM winche_asset_channels WHERE name = $1 FOR UPDATE", conn, tx))
        {
            get.Parameters.AddWithValue(channel);
            from = await get.ExecuteScalarAsync(ct) as int?;
        }
        await using (var up = new NpgsqlCommand("""
            INSERT INTO winche_asset_channels (name, release, updated_at, updated_by) VALUES ($1, $2, $3, $4)
            ON CONFLICT (name) DO UPDATE SET release = excluded.release, updated_at = excluded.updated_at, updated_by = excluded.updated_by
            """, conn, tx))
        {
            up.Parameters.AddWithValue(channel);
            up.Parameters.AddWithValue(release);
            up.Parameters.AddWithValue(at);
            up.Parameters.AddWithValue((object?)by ?? DBNull.Value);
            await up.ExecuteNonQueryAsync(ct);
        }
        await using var log = new NpgsqlCommand("INSERT INTO winche_asset_channel_moves (channel, from_release, to_release, moved_at, moved_by) VALUES ($1, $2, $3, $4, $5)", conn, tx);
        log.Parameters.AddWithValue(channel);
        log.Parameters.AddWithValue((object?)from ?? DBNull.Value);
        log.Parameters.AddWithValue(release);
        log.Parameters.AddWithValue(at);
        log.Parameters.AddWithValue((object?)by ?? DBNull.Value);
        await log.ExecuteNonQueryAsync(ct);
    }

    internal async Task<bool> RemoveChannelAsync(string name, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("DELETE FROM winche_asset_channels WHERE name = $1");
        cmd.Parameters.AddWithValue(name);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<IReadOnlyList<AssetChannelMove>> HistoryAsync(string channel, int limit, CancellationToken ct)
    {
        await InitializeAsync(ct);
        await using var cmd = db.CreateCommand("SELECT channel, from_release, to_release, moved_at, moved_by FROM winche_asset_channel_moves WHERE channel = $1 ORDER BY id DESC LIMIT $2");
        cmd.Parameters.AddWithValue(channel);
        cmd.Parameters.AddWithValue(limit);
        var list = new List<AssetChannelMove>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) list.Add(new(r.GetString(0), r.IsDBNull(1) ? null : r.GetInt32(1), r.GetInt32(2), r.GetFieldValue<DateTimeOffset>(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }
}
