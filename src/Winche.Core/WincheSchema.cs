using Npgsql;

namespace Winche.Core;

/// <summary>
/// Creates and upgrades a package's Postgres tables, the same way for every Winche package: numbered steps, applied
/// in order under one advisory lock (so many servers starting at once don't race), each in its own transaction, with
/// the version reached kept in <c>winche_schema</c>.
/// <para>
/// Steps are the package's history: append new ones, never change applied ones. Write them so a database that already
/// has the tables (from before versions were kept) takes them harmlessly: <c>CREATE TABLE IF NOT EXISTS</c>,
/// <c>ADD COLUMN IF NOT EXISTS</c>.
/// </para>
/// </summary>
public static class WincheSchema
{
    /// <summary>
    /// Brings <paramref name="component"/> (e.g. <c>webhooks</c>) up to the last of <paramref name="steps"/>, and returns
    /// the version it is at.
    /// </summary>
    public static async Task<int> EnsureAsync(NpgsqlDataSource source, string component, IReadOnlyList<string> steps, CancellationToken ct = default)
    {
        // One connection: it takes the lock, does the work, and lets go (or drops it, should it break).
        await using var conn = await source.OpenConnectionAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_lock(@k)", conn))
        {
            take.Parameters.AddWithValue("k", AdvisoryLease.KeyFor("schema:" + component));
            await take.ExecuteNonQueryAsync(ct);
        }
        try
        {
            return await ApplyAsync(conn, component, steps, ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@k)", conn);
            release.Parameters.AddWithValue("k", AdvisoryLease.KeyFor("schema:" + component));
            try { await release.ExecuteNonQueryAsync(CancellationToken.None); }
            catch { NpgsqlConnection.ClearPool(conn); }   // never pool a connection that may still hold the lock
        }
    }

    private static async Task<int> ApplyAsync(NpgsqlConnection conn, string component, IReadOnlyList<string> steps, CancellationToken ct)
    {
        // The bookkeeping table is every component's: made under a lock of its own, as two components (each holding only
        // its own lock) may start at once, and CREATE TABLE IF NOT EXISTS races with itself.
        await using (var made = await conn.BeginTransactionAsync(ct))
        {
            await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", conn, made))
            {
                take.Parameters.AddWithValue("k", AdvisoryLease.KeyFor("schema:winche_schema"));
                await take.ExecuteNonQueryAsync(ct);
            }
            await using (var create = new NpgsqlCommand("""
                CREATE TABLE IF NOT EXISTS winche_schema (
                  component text PRIMARY KEY,
                  version int NOT NULL,
                  updated_at timestamptz NOT NULL DEFAULT now())
                """, conn, made))
                await create.ExecuteNonQueryAsync(ct);
            await made.CommitAsync(ct);
        }

        await using var read = new NpgsqlCommand("SELECT version FROM winche_schema WHERE component = @c", conn);
        read.Parameters.AddWithValue("c", component);
        var version = await read.ExecuteScalarAsync(ct) is int v ? v : 0;

        for (; version < steps.Count; version++)
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var step = new NpgsqlCommand(steps[version], conn, tx)) await step.ExecuteNonQueryAsync(ct);
            await using (var mark = new NpgsqlCommand("""
                INSERT INTO winche_schema (component, version) VALUES (@c, @v)
                ON CONFLICT (component) DO UPDATE SET version = EXCLUDED.version, updated_at = now()
                """, conn, tx))
            {
                mark.Parameters.AddWithValue("c", component);
                mark.Parameters.AddWithValue("v", version + 1);
                await mark.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return version;
    }
}

/// <summary>SQL helpers every Winche package needs.</summary>
public static class WincheSql
{
    /// <summary>Escapes <c>\</c>, <c>%</c> and <c>_</c> so text matches literally in <c>LIKE … ESCAPE '\'</c>.</summary>
    public static string EscapeLike(string value) =>
        string.IsNullOrEmpty(value) ? value : value.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
