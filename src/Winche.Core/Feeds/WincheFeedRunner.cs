using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Winche.Core.Feeds;

/// <summary>The meter for every feed's consumers (<see cref="MeterName"/>), tagged with feed and consumer.</summary>
public static class WincheFeedMetrics
{
    public const string MeterName = "Winche.Feed";
    internal static readonly Meter Meter = new(MeterName);
    internal static readonly Counter<long> Delivered = Meter.CreateCounter<long>("winche.feed.delivered", null, "Records delivered to durable consumers");
    internal static readonly Histogram<double> Lag = Meter.CreateHistogram<double>("winche.feed.lag", "s", "Commit to delivery of a batch's newest record");
    internal static readonly Counter<long> Failures = Meter.CreateCounter<long>("winche.feed.failures", null, "Failed deliveries (retried)");
    internal static readonly Counter<long> DeadLetters = Meter.CreateCounter<long>("winche.feed.dead_letters", null, "Records consumers gave up on");

    internal static KeyValuePair<string, object?>[] Tags(string feed, string consumer) => [new("feed", feed), new("consumer", consumer)];
}

/// <summary>
/// Runs one durable consumer of a feed: on one node at a time (a lease, so any node can take over), in commit order,
/// at least once, resuming from its saved position. A failing record is retried with backoff, holding the consumer
/// back, until it has failed for <see cref="WincheFeedOptions.DeadLetterAfter"/>; then it becomes a dead letter and the
/// consumer moves on. A new consumer starts from now (no replay of history).
/// </summary>
public sealed class WincheFeedRunner<TRecord>(
    WincheFeed<TRecord> feed,
    string consumer,
    Func<IReadOnlyList<TRecord>, CancellationToken, Task> deliver,
    WincheFeedOptions options,
    ILogger logger) where TRecord : IFeedRecord
{
    /// <summary>Runs until <paramref name="ct"/> stops it; <paramref name="waitForWake"/> returns when there may be new records.</summary>
    public async Task RunAsync(Func<CancellationToken, Task> waitForWake, CancellationToken ct)
    {
        var lockName = $"feed-consumer:{feed.Name}:{consumer}";
        while (!ct.IsCancellationRequested)
        {
            AdvisoryLease? lease = null;
            try { lease = await AdvisoryLease.TryAcquireAsync(feed.Db, lockName, options.LeaseHeartbeat, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning(ex, "Consumer '{Consumer}' of {Feed} could not try for its lock", consumer, feed.Name); }
            if (lease is null)
            {
                try { await Task.Delay(options.LeaderRetryInterval, ct); } catch (OperationCanceledException) { break; }
                continue;
            }
            await using (lease)
            {
                logger.LogInformation("Consumer '{Consumer}' of {Feed} is active on this node", consumer, feed.Name);
                using var active = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Lost);
                await DeliverAsync(waitForWake, active.Token);
                if (!ct.IsCancellationRequested)
                    logger.LogWarning("Consumer '{Consumer}' of {Feed} lost its lock; standing by", consumer, feed.Name);
            }
        }
    }

    private enum Step { Delivered, Empty, Failed, Stopped }

    /// <summary>A delivery loop's state: where it is, the batch being retried one at a time, and failures in a row.</summary>
    private sealed class Loop
    {
        public FeedPosition? Cursor;
        public FeedPosition? OneAtATimeThrough;   // the end of the batch that failed
        public int Failures;
        public int Delivered;   // records delivered so far
    }

    private async Task DeliverAsync(Func<CancellationToken, Task> waitForWake, CancellationToken ct)
    {
        var loop = new Loop();
        while (!ct.IsCancellationRequested)
        {
            switch (await StepAsync(loop, ct))
            {
                case Step.Empty:
                    try { await waitForWake(ct); } catch (OperationCanceledException) { return; }
                    break;
                case Step.Failed:
                    try { await Task.Delay(Backoff(loop.Failures), ct); } catch (OperationCanceledException) { return; }
                    break;
                case Step.Stopped:
                    return;
            }
        }
    }

    /// <summary>
    /// Delivers what's waiting now, without the lease or waiting: until the feed is caught up or a record fails (which
    /// is recorded, and dead-lettered when due, as in a run). Returns how many records were delivered. For tests and
    /// tools; don't call it while the consumer runs on another node.
    /// </summary>
    public async Task<int> DeliverPendingAsync(CancellationToken ct = default)
    {
        var loop = new Loop();
        while (!ct.IsCancellationRequested && await StepAsync(loop, ct) == Step.Delivered) { }
        return loop.Delivered;
    }

    private async Task<Step> StepAsync(Loop loop, CancellationToken ct)
    {
        var tags = WincheFeedMetrics.Tags(feed.Name, consumer);
        IReadOnlyList<TRecord> records;
        try
        {
            // Inside the retry loop, so a database hiccup backs off and logs instead of ending the runner.
            loop.Cursor ??= await BootstrapAsync(ct);
            if (loop.OneAtATimeThrough is { } through && loop.Cursor.Value.CompareTo(through) >= 0) loop.OneAtATimeThrough = null;
            records = await feed.ReadAfterAsync(loop.Cursor.Value, loop.OneAtATimeThrough is null ? options.BatchSize : 1, ct);
            if (records.Count == 0)
            {
                loop.Failures = 0;
                loop.OneAtATimeThrough = null;
                return Step.Empty;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Step.Stopped; }
        catch (Exception ex)
        {
            loop.Failures++;
            logger.LogError(ex, "Consumer '{Consumer}' could not read {Feed} (attempt {Attempt})", consumer, feed.Name, loop.Failures);
            return Step.Failed;
        }

        try
        {
            await deliver(records, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return Step.Stopped; }
        catch (Exception caught)
        {
            loop.Failures++;
            WincheFeedMetrics.Failures.Add(1, tags);
            var ex = caught;
            TRecord record;
            if (caught is FeedDeliveryException { Delivered: var delivered } partial && delivered < records.Count)
            {
                // The consumer said which record failed: keep the ones before it, retry only it.
                ex = partial.InnerException ?? partial;
                record = records[delivered];
                if (delivered > 0)
                {
                    loop.Delivered += delivered;
                    loop.Cursor = records[delivered - 1].Position;
                    if (!await TrySaveAsync(loop.Cursor.Value, ct)) return Step.Stopped;
                }
            }
            else if (records.Count > 1)
            {
                // Don't redo the whole batch on every retry: go one record at a time to find the one that fails.
                loop.OneAtATimeThrough = records[^1].Position;
                logger.LogWarning(caught, "Consumer '{Consumer}' of {Feed} failed on a batch of {Count}; delivering them one at a time",
                    consumer, feed.Name, records.Count);
                return Step.Delivered;   // not a failure yet: the next step retries one record
            }
            else record = records[0];

            if (await TryDeadLetterAsync(record, ex, ct))
            {
                loop.Cursor = record.Position;
                loop.Failures = 0;
                return Step.Delivered;
            }
            logger.LogError(ex, "Consumer '{Consumer}' of {Feed} failed on {Record}, attempt {Attempt}; retrying in {Backoff}",
                consumer, feed.Name, record.Describe(), loop.Failures, Backoff(loop.Failures));
            return Step.Failed;
        }

        loop.Cursor = records[^1].Position;
        loop.Failures = 0;
        loop.Delivered += records.Count;
        WincheFeedMetrics.Delivered.Add(records.Count, tags);
        WincheFeedMetrics.Lag.Record(Math.Max(0, (DateTimeOffset.UtcNow - records[^1].At).TotalSeconds), tags);
        // Saved after delivery: a failed save means the records may be delivered again after a restart, not lost.
        return await TrySaveAsync(loop.Cursor.Value, ct) ? Step.Delivered : Step.Stopped;
    }

    /// <summary>
    /// Saves the position after a delivery, even while stopping: what was delivered stays delivered (with its own short
    /// time limit, so a stop isn't held up). False when the runner is stopping.
    /// </summary>
    private async Task<bool> TrySaveAsync(FeedPosition position, CancellationToken ct)
    {
        try
        {
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await feed.SavePositionAsync(consumer, position, save.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Consumer '{Consumer}' of {Feed} could not save its position; records may be delivered again after a restart",
                consumer, feed.Name);
        }
        return !ct.IsCancellationRequested;
    }

    private async Task<bool> TryDeadLetterAsync(TRecord record, Exception error, CancellationToken ct)
    {
        try
        {
            var (attempts, failingFor) = await feed.RecordFailureAsync(consumer, record, error.ToString(), ct);
            if (options.DeadLetterAfter is not { } limit || failingFor < limit) return false;
            await feed.DeadLetterAsync(consumer, record, ct);
            WincheFeedMetrics.DeadLetters.Add(1, WincheFeedMetrics.Tags(feed.Name, consumer));
            logger.LogError(error, "Consumer '{Consumer}' of {Feed} gave up on {Record} after {Attempts} attempts over {FailingFor}; it's a dead letter now",
                consumer, feed.Name, record.Describe(), attempts, failingFor);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Consumer '{Consumer}' of {Feed} could not record a failure", consumer, feed.Name);
            return false;
        }
    }

    private static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5) - 1)));

    private async Task<FeedPosition> BootstrapAsync(CancellationToken ct)
    {
        if (await feed.GetPositionAsync(consumer, ct) is { } saved) return saved;
        var head = await feed.GetHeadAsync(ct);
        await feed.SavePositionAsync(consumer, head, ct);
        return head;
    }
}

/// <summary>
/// Wakes consumers when a feed may have new records: on a Postgres notification on <c>channel</c> (sent when records
/// are written), or after <c>poll</c> at the latest, so a missed notification only delays.
/// </summary>
public sealed class WincheFeedWake(NpgsqlDataSource db, string channel, TimeSpan poll, ILogger logger) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _listening;

    /// <summary>Returns when woken, or after the poll interval.</summary>
    public async Task WaitAsync(CancellationToken ct)
    {
        _listening ??= Task.Run(() => ListenAsync(_stop.Token));
        var signal = Volatile.Read(ref _signal);
        try { await signal.Task.WaitAsync(poll, ct); }
        catch (TimeoutException) { }
    }

    /// <summary>Wakes every waiter now (e.g. after a write on this node).</summary>
    public void Wake() => Interlocked.Exchange(ref _signal, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await db.OpenConnectionAsync(ct);
                conn.Notification += (_, _) => Wake();
                await using (var listen = conn.CreateCommand())
                {
                    listen.CommandText = $"LISTEN \"{channel}\"";
                    await listen.ExecuteNonQueryAsync(ct);
                }
                while (!ct.IsCancellationRequested) await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Listening for {Channel} failed; polling until it's back", channel);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_listening is not null) try { await _listening; } catch { }
        _stop.Dispose();
    }
}
