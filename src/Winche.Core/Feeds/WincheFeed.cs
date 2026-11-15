using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Winche.Core.Feeds;

/// <summary>
/// A feed: an append-only table of changes (<c>seq bigint</c>, <c>txid xid8</c>, a commit time, and the package's own
/// columns) read in commit order, and its durable consumers' cursors, failures and dead letters, kept in the tables
/// every feed shares (<see cref="WincheFeedTables"/>). Packages with their own change table (the database) describe it
/// here; others use <see cref="WincheEventFeed"/>'s ready-made one.
/// </summary>
public sealed class WincheFeed<TRecord> where TRecord : IFeedRecord
{
    private readonly NpgsqlDataSource _db;
    private readonly string _columns;
    private readonly string _at;
    private readonly Func<NpgsqlDataReader, TRecord> _read;
    private readonly Func<TRecord, JsonObject> _toJson;
    private readonly Func<JsonObject, TRecord> _fromJson;

    /// <param name="name">The feed's name, unique among feeds (e.g. <c>database</c>, <c>storage</c>).</param>
    /// <param name="table">Its change table: has <c>seq</c>, <c>txid</c> (xid8) and <paramref name="atColumn"/>.</param>
    /// <param name="columns">The columns <paramref name="read"/> reads, in order (it reads them by ordinal).</param>
    /// <param name="read">A record from a row of <paramref name="columns"/>.</param>
    /// <param name="toJson">A record as kept with a dead letter; <paramref name="fromJson"/> reads it back.</param>
    public WincheFeed(NpgsqlDataSource db, string name, string table, string columns, string atColumn,
        Func<NpgsqlDataReader, TRecord> read, Func<TRecord, JsonObject> toJson, Func<JsonObject, TRecord> fromJson)
    {
        _db = db;
        Name = name;
        Table = table;
        _columns = columns;
        _at = atColumn;
        _read = read;
        _toJson = toJson;
        _fromJson = fromJson;
    }

    public string Name { get; }
    public string Table { get; }
    internal NpgsqlDataSource Db => _db;

    // ── reading ───────────────────────────────────────────────────────────────────

    /// <summary>Up to <paramref name="limit"/> records after <paramref name="after"/>, of committed transactions only.</summary>
    public async Task<IReadOnlyList<TRecord>> ReadAfterAsync(FeedPosition after, int limit, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT {_columns}
            FROM {Table}
            WHERE (txid, seq) > ($1::text::xid8, $2)
              AND txid < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY txid, seq
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(after.TxText);
        cmd.Parameters.AddWithValue(after.Seq);
        cmd.Parameters.AddWithValue(limit);
        var records = new List<TRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) records.Add(_read(reader));
        return records;
    }

    /// <summary>Where the feed is now: past every committed record, before any still to commit.</summary>
    public async Task<FeedPosition> GetHeadAsync(CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand("SELECT pg_snapshot_xmin(pg_current_snapshot())::text");
        var xmin = ulong.Parse((string)(await cmd.ExecuteScalarAsync(ct))!, CultureInfo.InvariantCulture);
        return new FeedPosition(xmin - 1, long.MaxValue);
    }

    // ── cursors and failures ──────────────────────────────────────────────────────

    private const string ClearFailure =
        "failing_txid = NULL, failing_seq = NULL, failing_since = NULL, failing_attempts = 0, failing_error = NULL";

    /// <summary>Where <paramref name="consumer"/> has delivered up to, or null if it never ran.</summary>
    public async Task<FeedPosition?> GetPositionAsync(string consumer, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand(
            $"SELECT seq, COALESCE(txid, '0')::text FROM {WincheFeedTables.Cursors} WHERE feed = $1 AND consumer = $2");
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(consumer);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new FeedPosition(ulong.Parse(reader.GetString(1), CultureInfo.InvariantCulture), reader.GetInt64(0));
    }

    /// <summary>Moves <paramref name="consumer"/> forward to <paramref name="position"/> (never back) and clears its failure.</summary>
    public async Task SavePositionAsync(string consumer, FeedPosition position, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($$"""
            INSERT INTO {{WincheFeedTables.Cursors}} AS c (feed, consumer, seq, txid, updated_at) VALUES ($1, $2, $3, $4::text::xid8, now())
            ON CONFLICT (feed, consumer) DO UPDATE SET seq = EXCLUDED.seq, txid = EXCLUDED.txid, updated_at = now(), {{ClearFailure}}
            WHERE (COALESCE(c.txid, '0'), c.seq) < (EXCLUDED.txid, EXCLUDED.seq)
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(position.Seq);
        cmd.Parameters.AddWithValue(position.TxText);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Notes that <paramref name="consumer"/> failed on <paramref name="record"/>: how many attempts, and for how long,
    /// it has been failing on that same record.
    /// </summary>
    public async Task<(int Attempts, TimeSpan FailingFor)> RecordFailureAsync(string consumer, TRecord record, string error, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            UPDATE {WincheFeedTables.Cursors} SET
                failing_since    = CASE WHEN failing_txid = $3::text::xid8 AND failing_seq = $4 THEN failing_since ELSE now() END,
                failing_attempts = CASE WHEN failing_txid = $3::text::xid8 AND failing_seq = $4 THEN failing_attempts + 1 ELSE 1 END,
                failing_txid = $3::text::xid8, failing_seq = $4, failing_error = $5
            WHERE feed = $1 AND consumer = $2
            RETURNING failing_attempts, EXTRACT(EPOCH FROM now() - failing_since)::float8
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(record.Position.TxText);
        cmd.Parameters.AddWithValue(record.Position.Seq);
        cmd.Parameters.AddWithValue(error.Length <= 8000 ? error : error[..8000]);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return (1, TimeSpan.Zero);
        return (reader.GetInt32(0), TimeSpan.FromSeconds(reader.GetDouble(1)));
    }

    /// <summary>Moves <paramref name="record"/> to the dead letters and <paramref name="consumer"/> past it, in one step.</summary>
    public async Task DeadLetterAsync(string consumer, TRecord record, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($$"""
            WITH failure AS (
                SELECT failing_error, failing_attempts, failing_since FROM {{WincheFeedTables.Cursors}} WHERE feed = $1 AND consumer = $2
            ), dead AS (
                INSERT INTO {{WincheFeedTables.DeadLetters}} (feed, consumer, txid, seq, record, error, attempts, first_failed_at)
                VALUES ($1, $2, $3::text::xid8, $4, $5::jsonb,
                        (SELECT failing_error FROM failure),
                        COALESCE((SELECT failing_attempts FROM failure), 0),
                        COALESCE((SELECT failing_since FROM failure), now()))
                ON CONFLICT (feed, consumer, txid, seq) DO NOTHING
            )
            UPDATE {{WincheFeedTables.Cursors}} AS c SET seq = $4, txid = $3::text::xid8, updated_at = now(), {{ClearFailure}}
            WHERE c.feed = $1 AND c.consumer = $2 AND (COALESCE(c.txid, '0'), c.seq) < ($3::text::xid8, $4)
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(consumer);
        cmd.Parameters.AddWithValue(record.Position.TxText);
        cmd.Parameters.AddWithValue(record.Position.Seq);
        cmd.Parameters.Add(new NpgsqlParameter { Value = _toJson(record).ToJsonString(), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gives each of <paramref name="consumers"/> the cursor of <paramref name="from"/>, unless a consumer whose name
    /// starts with <paramref name="familyPrefix"/> exists already: for splitting one consumer into several without
    /// losing or replaying changes.
    /// </summary>
    public async Task<int> InheritCursorAsync(string from, string familyPrefix, IReadOnlyList<string> consumers, CancellationToken ct = default)
    {
        if (consumers.Count == 0) return 0;
        await using var cmd = _db.CreateCommand($"""
            INSERT INTO {WincheFeedTables.Cursors} (feed, consumer, seq, txid, updated_at)
            SELECT $1, n.name, old.seq, COALESCE(old.txid, '0'), now()
            FROM unnest($4::text[]) AS n(name)
            CROSS JOIN {WincheFeedTables.Cursors} AS old
            WHERE old.feed = $1 AND old.consumer = $2
              AND NOT EXISTS (SELECT 1 FROM {WincheFeedTables.Cursors} WHERE feed = $1 AND starts_with(consumer, $3))
            ON CONFLICT (feed, consumer) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(from);
        cmd.Parameters.AddWithValue(familyPrefix);
        cmd.Parameters.AddWithValue(consumers.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── status, dead letters, pruning ─────────────────────────────────────────────

    /// <summary>How each of <paramref name="consumers"/> is doing: position, lag behind the oldest waiting record, failure, dead letters.</summary>
    public async Task<IReadOnlyList<FeedConsumerStatus>> GetStatusAsync(IReadOnlyList<string> consumers, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT n.name, c.seq, COALESCE(c.txid, '0')::text,
                   (SELECT x.{_at} FROM {Table} AS x
                    WHERE c.consumer IS NOT NULL
                      AND (x.txid, x.seq) > (COALESCE(c.txid, '0'), c.seq)
                      AND x.txid < pg_snapshot_xmin(pg_current_snapshot())
                    ORDER BY x.txid, x.seq LIMIT 1),
                   c.failing_since, COALESCE(c.failing_attempts, 0), c.failing_error,
                   (SELECT count(*) FROM {WincheFeedTables.DeadLetters} AS d WHERE d.feed = $1 AND d.consumer = n.name),
                   now()
            FROM unnest($2::text[]) WITH ORDINALITY AS n(name, i)
            LEFT JOIN {WincheFeedTables.Cursors} AS c ON c.feed = $1 AND c.consumer = n.name
            ORDER BY n.i
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(consumers.ToArray());
        var statuses = new List<FeedConsumerStatus>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var oldest = reader.IsDBNull(3) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(3);
            var now = reader.GetFieldValue<DateTimeOffset>(8);
            statuses.Add(new FeedConsumerStatus(
                Name,
                reader.GetString(0),
                reader.IsDBNull(1) ? null : new FeedPosition(ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt64(1)),
                oldest,
                oldest is { } o && now > o ? now - o : TimeSpan.Zero,
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetInt64(7)));
        }
        return statuses;
    }

    /// <summary>Dead letters of this feed (of <paramref name="consumer"/>, if given), oldest first, after <paramref name="after"/>.</summary>
    public async Task<IReadOnlyList<FeedDeadLetter>> GetDeadLettersAsync(string? consumer = null, FeedPosition? after = null, int limit = 100, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            SELECT id, consumer, txid::text, seq, record, error, attempts, first_failed_at, dead_lettered_at
            FROM {WincheFeedTables.DeadLetters}
            WHERE feed = $1 AND ($2::text IS NULL OR consumer = $2)
              AND ($3::text IS NULL OR (txid, seq) > ($3::text::xid8, $4))
            ORDER BY txid, seq, id
            LIMIT $5
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)consumer ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = after is { } a ? a.TxText : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(after?.Seq ?? 0L);
        cmd.Parameters.AddWithValue(limit);
        var letters = new List<FeedDeadLetter>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            letters.Add(new FeedDeadLetter(
                reader.GetInt64(0), Name, reader.GetString(1),
                new FeedPosition(ulong.Parse(reader.GetString(2), CultureInfo.InvariantCulture), reader.GetInt64(3)),
                JsonNode.Parse(reader.GetString(4))!.AsObject(),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetFieldValue<DateTimeOffset>(8)));
        return letters;
    }

    /// <summary>The record a dead letter holds.</summary>
    public TRecord RecordOf(FeedDeadLetter letter) => _fromJson(letter.Record);

    /// <summary>Removes dead letters: one (<paramref name="id"/>), or all of <paramref name="consumer"/>, or all of this feed.</summary>
    public async Task<int> DeleteDeadLettersAsync(long? id = null, string? consumer = null, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            DELETE FROM {WincheFeedTables.DeadLetters}
            WHERE feed = $1 AND ($2::bigint IS NULL OR id = $2) AND ($3::text IS NULL OR consumer = $3)
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)consumer ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Delivers <paramref name="consumer"/>'s dead letters again through <paramref name="deliver"/>, oldest first; each
    /// delivered one is removed, each failing one kept (with its new error and one more attempt).
    /// </summary>
    public async Task<FeedReplayResult> ReplayDeadLettersAsync(string consumer, Func<TRecord, CancellationToken, Task> deliver, CancellationToken ct = default)
    {
        int replayed = 0, failed = 0;
        FeedPosition? after = null;
        while (true)
        {
            var page = await GetDeadLettersAsync(consumer, after, 100, ct);
            if (page.Count == 0) break;
            foreach (var letter in page)
            {
                try
                {
                    await deliver(RecordOf(letter), ct);
                    await DeleteDeadLettersAsync(letter.Id, ct: ct);
                    replayed++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // Kept, with the new error and one more attempt.
                    var cause = ex is FeedDeliveryException { InnerException: { } inner } ? inner : ex;
                    await using var cmd = _db.CreateCommand($"UPDATE {WincheFeedTables.DeadLetters} SET attempts = attempts + 1, error = $2 WHERE id = $1");
                    cmd.Parameters.AddWithValue(letter.Id);
                    cmd.Parameters.AddWithValue(cause.ToString());
                    await cmd.ExecuteNonQueryAsync(ct);
                    failed++;
                }
                after = letter.Position;
            }
        }
        return new FeedReplayResult(replayed, failed);
    }

    /// <summary>
    /// Removes records older than <paramref name="cutoff"/> that every one of <paramref name="consumers"/> has passed
    /// (all of them, when older than <paramref name="hardCutoff"/>); returns how many went.
    /// </summary>
    public async Task<int> PruneAsync(DateTimeOffset cutoff, DateTimeOffset hardCutoff, IReadOnlyList<string> consumers, CancellationToken ct = default)
    {
        await using var cmd = _db.CreateCommand($"""
            WITH slowest AS (
                SELECT COALESCE(txid, '0') AS txid, seq FROM {WincheFeedTables.Cursors}
                WHERE feed = $1 AND consumer = ANY($4) ORDER BY 1, 2 LIMIT 1
            )
            DELETE FROM {Table} AS c
            WHERE c.{_at} < $2
              AND (c.{_at} < $3 OR NOT EXISTS (SELECT 1 FROM slowest) OR (c.txid, c.seq) <= (SELECT txid, seq FROM slowest))
            """);
        cmd.Parameters.AddWithValue(Name);
        cmd.Parameters.AddWithValue(cutoff);
        cmd.Parameters.AddWithValue(hardCutoff);
        cmd.Parameters.AddWithValue(consumers.ToArray());
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
