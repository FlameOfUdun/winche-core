using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;

namespace Winche.Core.Feeds;

/// <summary>One change in an event feed: what happened (<see cref="Type"/>) to what (<see cref="Path"/>), with <see cref="Data"/>.</summary>
public sealed record FeedEvent(long Seq, ulong TxId, DateTimeOffset At, string Type, string Path, JsonObject? Data) : IFeedRecord
{
    public FeedPosition Position => new(TxId, Seq);
    public string Describe() => $"{Type} {Path} (#{Seq})";
}

/// <summary>
/// A ready-made feed for packages without a change table of their own: <see cref="AppendAsync"/> writes an event in
/// the caller's transaction (so it exists exactly when the change does), and <see cref="Open"/> reads them for durable
/// consumers (<see cref="WincheFeedRunner{TRecord}"/>).
/// </summary>
public static class WincheEventFeed
{
    /// <summary>The schema step that creates the feed table <paramref name="table"/>.</summary>
    public static string TableSql(string table) => $"""
        CREATE TABLE IF NOT EXISTS {table} (
            seq   bigserial   NOT NULL,
            txid  xid8        NOT NULL DEFAULT pg_current_xact_id(),
            at    timestamptz NOT NULL DEFAULT now(),
            type  text        NOT NULL,
            path  text        NOT NULL,
            data  jsonb       NULL,
            PRIMARY KEY (txid, seq)
        );
        CREATE INDEX IF NOT EXISTS idx_{table}_at ON {table} (at);
        """;

    /// <summary>The Postgres channel <see cref="AppendAsync"/> notifies for <paramref name="table"/>.</summary>
    public static string Channel(string table) => table + "_events";

    /// <summary>Writes an event in <paramref name="tx"/> and notifies listeners when it commits.</summary>
    public static async Task AppendAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, string table, string type, string path, JsonObject? data,
        CancellationToken ct = default)
    {
        await using var cmd = new NpgsqlCommand($"WITH e AS (INSERT INTO {table} (type, path, data) VALUES ($1, $2, $3) RETURNING 1) SELECT pg_notify($4, '') FROM e", conn, tx);
        cmd.Parameters.AddWithValue(type);
        cmd.Parameters.AddWithValue(path);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)data?.ToJsonString() ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.AddWithValue(Channel(table));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The feed <paramref name="name"/> over <paramref name="table"/>.</summary>
    public static WincheFeed<FeedEvent> Open(NpgsqlDataSource db, string name, string table) => new(
        db, name, table, "seq, txid::text, at, type, path, data", "at",
        r => new FeedEvent(
            r.GetInt64(0), ulong.Parse(r.GetString(1), CultureInfo.InvariantCulture), r.GetFieldValue<DateTimeOffset>(2),
            r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : JsonNode.Parse(r.GetString(5))?.AsObject()),
        e => new JsonObject
        {
            ["seq"] = e.Seq, ["txid"] = e.TxId.ToString(CultureInfo.InvariantCulture), ["at"] = e.At.ToString("O"),
            ["type"] = e.Type, ["path"] = e.Path, ["data"] = e.Data?.DeepClone(),
        },
        j => new FeedEvent(
            (long)j["seq"]!, ulong.Parse((string)j["txid"]!, CultureInfo.InvariantCulture), DateTimeOffset.Parse((string)j["at"]!, CultureInfo.InvariantCulture),
            (string)j["type"]!, (string)j["path"]!, j["data"]?.AsObject().DeepClone().AsObject()));
}
