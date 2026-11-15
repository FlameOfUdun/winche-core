using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Winche.Core;

/// <summary>
/// The settings, kept in the project's database (table <c>winche_settings</c>): every change to every area is a row,
/// so the history is complete and any version can be restored. The latest row of an area is the one in force; a row
/// with no document means "back to the code's values".
/// </summary>
public sealed class SettingsStore
{
    private readonly NpgsqlDataSource _db;
    private readonly IReadOnlyDictionary<string, SettingsArea> _areas;
    private readonly ILogger<SettingsStore> _logger;
    private long _seen;

    internal SettingsStore(NpgsqlDataSource db, IEnumerable<SettingsArea> areas, ILogger<SettingsStore> logger)
    {
        _db = db;
        _areas = areas.ToDictionary(a => a.Name, StringComparer.Ordinal);
        _logger = logger;
    }

    /// <summary>The areas the app registered: what the console can show and change.</summary>
    public IReadOnlyCollection<SettingsArea> Areas => [.. _areas.Values];

    public SettingsArea? Area(string name) => _areas.GetValueOrDefault(name);

    /// <summary>Saves a new version of <paramref name="area"/> after its checks pass; in force at once here, within seconds elsewhere.</summary>
    public Task<SettingsVersion> SaveAsync(string area, JsonNode document, string? by, string? reason, CancellationToken ct = default) =>
        SaveAsync(area, document, by, reason, basedOn: null, ct);

    /// <summary>
    /// Saves a new version made from version <paramref name="basedOn"/> (0: none saved yet). Refused with a
    /// <see cref="SettingsConflictException"/> when someone saved another since, so their change isn't lost; null: as is.
    /// </summary>
    public async Task<SettingsVersion> SaveAsync(string area, JsonNode document, string? by, string? reason, int? basedOn, CancellationToken ct = default)
    {
        var a = Require(area);
        var errors = a.Validate(document);
        if (errors.Count > 0) throw new SettingsValidationException(area, errors);
        return await InsertAsync(a, document, by, reason, null, ct, basedOn);
    }

    /// <summary>Goes back to the code's values (a new version, so it shows in the history and can be undone).</summary>
    public Task<SettingsVersion> ResetAsync(string area, string? by, string? reason, CancellationToken ct = default) =>
        InsertAsync(Require(area), null, by, reason, null, ct);

    /// <summary>Makes an earlier version current again, as a new version.</summary>
    public async Task<SettingsVersion> RestoreAsync(long id, string? by, string? reason, CancellationToken ct = default)
    {
        var old = await GetAsync(id, ct) ?? throw new KeyNotFoundException($"There's no settings version {id}.");
        var a = Require(old.Area);
        if (old.Document is { } doc && a.Validate(doc) is { Count: > 0 } errors)
            throw new SettingsValidationException(old.Area, errors);   // the app changed since: the old shape no longer fits
        return await InsertAsync(a, old.Document?.DeepClone(), by, reason, old.Id, ct);
    }

    /// <summary>Changes, newest first: one area's, or all of them.</summary>
    public async Task<IReadOnlyList<SettingsVersion>> HistoryAsync(string? area = null, int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT id, area, number, document::text, at, changed_by, reason, restored_from FROM winche_settings
            {(area is null ? "" : "WHERE area = @area")} ORDER BY id DESC LIMIT @limit
            """);
        if (area is not null) cmd.Parameters.AddWithValue("area", area);
        cmd.Parameters.AddWithValue("limit", Math.Clamp(limit, 1, 1000));
        return await ReadAsync(cmd, ct);
    }

    public async Task<SettingsVersion?> GetAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT id, area, number, document::text, at, changed_by, reason, restored_from FROM winche_settings WHERE id = @id");
        cmd.Parameters.AddWithValue("id", id);
        return (await ReadAsync(cmd, ct)).FirstOrDefault();
    }

    private async Task<SettingsVersion> InsertAsync(SettingsArea area, JsonNode? document, string? by, string? reason, long? restoredFrom, CancellationToken ct,
        int? basedOn = null)
    {
        // With basedOn: only when that's still the latest (nothing inserted otherwise).
        await using var cmd = _db.CreateCommand("""
            INSERT INTO winche_settings (area, number, document, at, changed_by, reason, restored_from)
            SELECT @area, latest + 1, @doc::jsonb, now(), @by, @reason, @from
            FROM (SELECT COALESCE((SELECT max(number) FROM winche_settings WHERE area = @area), 0) AS latest) l
            WHERE @based::int IS NULL OR latest = @based::int
            RETURNING id, area, number, document::text, at, changed_by, reason, restored_from
            """);
        cmd.Parameters.Add(new NpgsqlParameter("based", NpgsqlDbType.Integer) { Value = (object?)basedOn ?? DBNull.Value });
        cmd.Parameters.AddWithValue("area", area.Name);
        cmd.Parameters.Add(new NpgsqlParameter("doc", NpgsqlDbType.Text) { Value = (object?)document?.ToJsonString() ?? DBNull.Value });
        cmd.Parameters.AddWithValue("by", (object?)Trim(by, 200) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("reason", (object?)Trim(reason, 500) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("from", (object?)restoredFrom ?? DBNull.Value);
        SettingsVersion saved;
        try
        {
            saved = (await ReadAsync(cmd, ct)).SingleOrDefault()
                ?? throw new SettingsConflictException(area.Name, await LatestNumberAsync(area.Name, ct));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new InvalidOperationException("Someone else saved these settings at the same moment. Look at the latest version and try again.");
        }
        area.Apply(saved);
        _logger.LogInformation("Settings {Area} v{Number} saved by {By}: {Reason}", area.Name, saved.Number, by ?? "unknown", reason ?? "no reason given");
        return saved;
    }

    private async Task<int> LatestNumberAsync(string area, CancellationToken ct)
    {
        await using var cmd = _db.CreateCommand("SELECT COALESCE(max(number), 0) FROM winche_settings WHERE area = @area");
        cmd.Parameters.AddWithValue("area", area);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    private Task? _loaded;
    private readonly object _loadLock = new();

    /// <summary>
    /// Makes sure the saved settings are loaded (once). Packages call it before work that depends on them at
    /// startup — e.g. the database syncs its indexes — since the background watcher may not have started yet.
    /// </summary>
    public Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        lock (_loadLock)
        {
            if (_loaded is { IsFaulted: false, IsCanceled: false }) return _loaded;
            return _loaded = InitializeCoreAsync(ct);
        }
    }

    internal Task InitializeAsync(CancellationToken ct) => EnsureLoadedAsync(ct);

    /// <summary>The settings and secrets tables, in the order they were introduced (<see cref="WincheSchema"/>).</summary>
    internal static readonly string[] Steps =
    [
        """
            CREATE TABLE IF NOT EXISTS winche_settings (
              id bigserial PRIMARY KEY,
              area text NOT NULL,
              number int NOT NULL,
              document jsonb NULL,
              at timestamptz NOT NULL,
              changed_by text NULL,
              reason text NULL,
              restored_from bigint NULL,
              UNIQUE (area, number)
            );
            CREATE TABLE IF NOT EXISTS winche_secrets (
              name text PRIMARY KEY,
              ciphertext bytea NOT NULL,
              set_at timestamptz NOT NULL,
              set_by text NULL
            );
        """,
    ];

    /// <summary>Makes the tables, then loads every area's latest version.</summary>
    private async Task InitializeCoreAsync(CancellationToken ct)
    {
        await WincheSchema.EnsureAsync(_db, "core", Steps, ct);
        await RefreshAsync(force: true, ct);
    }

    /// <summary>Applies versions saved by other instances since the last look. Cheap when nothing changed.</summary>
    internal async Task RefreshAsync(bool force, CancellationToken ct)
    {
        await using (var max = _db.CreateCommand("SELECT COALESCE(max(id), 0) FROM winche_settings"))
        {
            var latest = (long)(await max.ExecuteScalarAsync(ct))!;
            if (!force && latest == _seen) return;
            _seen = latest;
        }
        await using var cmd = _db.CreateCommand("""
            SELECT DISTINCT ON (area) id, area, number, document::text, at, changed_by, reason, restored_from
            FROM winche_settings ORDER BY area, id DESC
            """);
        var latestByArea = (await ReadAsync(cmd, ct)).ToDictionary(v => v.Area);
        foreach (var area in _areas.Values)
        {
            var saved = latestByArea.GetValueOrDefault(area.Name);
            if (saved?.Id == area.Version?.Id && !force) continue;
            try { area.Apply(saved); }
            catch (Exception ex)
            {
                // A saved document the app can no longer read: keep what's in force, and say so.
                _logger.LogError(ex, "Settings {Area} v{Number} can't be applied; the previous values stay in force.", area.Name, saved?.Number);
            }
        }
    }

    private static async Task<List<SettingsVersion>> ReadAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var list = new List<SettingsVersion>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(new SettingsVersion(
                reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3)),
                new DateTimeOffset(reader.GetDateTime(4), TimeSpan.Zero),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetInt64(7)));
        return list;
    }

    private SettingsArea Require(string area) =>
        _areas.GetValueOrDefault(area) ?? throw new KeyNotFoundException($"There are no '{area}' settings in this app.");

    private static string? Trim(string? s, int max) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().Length <= max ? s.Trim() : s.Trim()[..max];
}
