using System.Text.Json.Nodes;

namespace Winche.Core.Feeds;

/// <summary>
/// Where a record sits in a feed: its transaction, then its sequence number. Transactions commit out of order, so a
/// feed is read in (transaction, sequence) order and only up to the oldest transaction still running: a reader never
/// passes a change that commits later behind it.
/// </summary>
public readonly record struct FeedPosition(ulong TxId, long Seq) : IComparable<FeedPosition>
{
    public int CompareTo(FeedPosition other) => TxId != other.TxId ? TxId.CompareTo(other.TxId) : Seq.CompareTo(other.Seq);

    internal string TxText => TxId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A record of a feed: where it is, when it was committed, and a line for logs.</summary>
public interface IFeedRecord
{
    FeedPosition Position { get; }
    DateTimeOffset At { get; }
    string Describe();
}

/// <summary>How one durable consumer of a feed is doing.</summary>
public sealed record FeedConsumerStatus(
    string Feed,
    string Name,
    FeedPosition? Position,
    DateTimeOffset? OldestPendingAt,
    TimeSpan Lag,
    DateTimeOffset? FailingSince,
    int FailingAttempts,
    string? LastError,
    long DeadLetters);

/// <summary>A record a consumer gave up on, as stored: <see cref="Record"/> is the feed's JSON for it.</summary>
public sealed record FeedDeadLetter(
    long Id,
    string Feed,
    string Consumer,
    FeedPosition Position,
    JsonObject Record,
    string? Error,
    int Attempts,
    DateTimeOffset FirstFailedAt,
    DateTimeOffset DeadLetteredAt);

/// <summary>What replaying dead letters did: delivered (and removed), and failed again (kept).</summary>
public sealed record FeedReplayResult(int Replayed, int Failed);

/// <summary>
/// Thrown by a consumer that delivered the first <see cref="Delivered"/> records of a batch and failed on the next:
/// those are kept, and only the failing one is retried.
/// </summary>
public sealed class FeedDeliveryException(int delivered, Exception inner)
    : Exception($"Delivery failed after {delivered} record(s): {inner.Message}", inner)
{
    public int Delivered { get; } = delivered;
}

/// <summary>How durable consumers run: batch size, lease, and when a failing record becomes a dead letter.</summary>
public sealed record WincheFeedOptions
{
    /// <summary>Records read and delivered at once (default 100).</summary>
    public int BatchSize { get; init; } = 100;

    /// <summary>How often the node running a consumer renews its claim (default 10 s).</summary>
    public TimeSpan LeaseHeartbeat { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often a node waiting for a consumer tries to take it over (default 5 s).</summary>
    public TimeSpan LeaderRetryInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>A record failing this long is moved to the dead letters and skipped (default 1 h; null: never).</summary>
    public TimeSpan? DeadLetterAfter { get; init; } = TimeSpan.FromHours(1);
}

/// <summary>The tables every feed's consumers share (schema component <c>feed</c>).</summary>
public static class WincheFeedTables
{
    /// <summary>One row per (feed, consumer): its position, and the record it's failing on, if any.</summary>
    public const string Cursors = "winche_feed_cursors";

    /// <summary>Records consumers gave up on.</summary>
    public const string DeadLetters = "winche_feed_dead_letters";

    internal static readonly string[] Steps =
    [
        $"""
        CREATE TABLE IF NOT EXISTS {Cursors} (
            feed             text        NOT NULL,
            consumer         text        NOT NULL,
            seq              bigint      NOT NULL,
            txid             xid8        NULL,
            updated_at       timestamptz NOT NULL DEFAULT now(),
            failing_txid     xid8        NULL,
            failing_seq      bigint      NULL,
            failing_since    timestamptz NULL,
            failing_attempts int         NOT NULL DEFAULT 0,
            failing_error    text        NULL,
            PRIMARY KEY (feed, consumer)
        );
        CREATE TABLE IF NOT EXISTS {DeadLetters} (
            id               bigserial   PRIMARY KEY,
            feed             text        NOT NULL,
            consumer         text        NOT NULL,
            txid             xid8        NOT NULL,
            seq              bigint      NOT NULL,
            record           jsonb       NOT NULL,
            error            text        NULL,
            attempts         int         NOT NULL DEFAULT 0,
            first_failed_at  timestamptz NOT NULL DEFAULT now(),
            dead_lettered_at timestamptz NOT NULL DEFAULT now(),
            UNIQUE (feed, consumer, txid, seq)
        );
        """,
    ];

    /// <summary>Creates the shared tables (idempotent; nodes take turns).</summary>
    public static Task EnsureAsync(Npgsql.NpgsqlDataSource db, CancellationToken ct = default) =>
        WincheSchema.EnsureAsync(db, "feed", Steps, ct);
}
