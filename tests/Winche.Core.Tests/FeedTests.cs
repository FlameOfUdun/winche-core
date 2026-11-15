using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Core.Feeds;

namespace Winche.Core.Tests;

[Collection("postgres")]
public class FeedTests(PostgresFixture pg)
{
    private const string Table = "test_events";

    private static async Task<(NpgsqlDataSource Db, WincheFeed<FeedEvent> Feed)> StartAsync(string connection)
    {
        var db = NpgsqlDataSource.Create(connection);
        await WincheFeedTables.EnsureAsync(db);
        await WincheSchema.EnsureAsync(db, "test-events", [WincheEventFeed.TableSql(Table)]);
        return (db, WincheEventFeed.Open(db, "test", Table));
    }

    private static async Task AppendAsync(NpgsqlDataSource db, string type, string path)
    {
        await using var conn = await db.OpenConnectionAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await WincheEventFeed.AppendAsync(conn, tx, Table, type, path, new JsonObject { ["n"] = path });
        await tx.CommitAsync();
    }

    /// <summary>Runs <paramref name="consumer"/> until <paramref name="done"/> or 10 s.</summary>
    private static async Task RunUntilAsync(WincheFeed<FeedEvent> feed, string consumer, Func<IReadOnlyList<FeedEvent>, CancellationToken, Task> deliver,
        Func<bool> done, WincheFeedOptions? options = null)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runner = new WincheFeedRunner<FeedEvent>(feed, consumer, deliver, options ?? new WincheFeedOptions(), NullLogger.Instance);
        var run = runner.RunAsync(ct => Task.Delay(20, ct), stop.Token);
        while (!done() && !stop.IsCancellationRequested) await Task.Delay(20);
        await stop.CancelAsync();
        await run;
    }

    [Fact]
    public async Task A_consumer_gets_every_event_after_it_started_in_commit_order_and_resumes()
    {
        var (db, feed) = await StartAsync(await pg.NewDatabaseAsync());
        await AppendAsync(db, "before", "x");   // a new consumer starts from now: never sees this
        await feed.SavePositionAsync("audit", await feed.GetHeadAsync());
        await AppendAsync(db, "created", "a");
        await AppendAsync(db, "created", "b");

        var seen = new List<string>();
        await RunUntilAsync(feed, "audit", (batch, _) => { seen.AddRange(batch.Select(e => e.Path)); return Task.CompletedTask; }, () => seen.Count >= 2);
        Assert.Equal(["a", "b"], seen);

        await AppendAsync(db, "deleted", "a");
        seen.Clear();
        await RunUntilAsync(feed, "audit", (batch, _) => { seen.AddRange(batch.Select(e => $"{e.Type} {e.Path}")); return Task.CompletedTask; }, () => seen.Count >= 1);
        Assert.Equal(["deleted a"], seen);   // resumed where it was
        var status = Assert.Single(await feed.GetStatusAsync(["audit"]));
        Assert.Equal((TimeSpan.Zero, 0L), (status.Lag, status.DeadLetters));
    }

    [Fact]
    public async Task A_record_that_keeps_failing_becomes_a_dead_letter_and_can_be_replayed()
    {
        var (db, feed) = await StartAsync(await pg.NewDatabaseAsync());
        await feed.SavePositionAsync("hook", await feed.GetHeadAsync());
        await AppendAsync(db, "created", "bad");
        await AppendAsync(db, "created", "good");

        var delivered = new List<string>();
        await RunUntilAsync(feed, "hook", (batch, _) =>
        {
            foreach (var (e, i) in batch.Select((e, i) => (e, i)))
            {
                if (e.Path == "bad") throw new FeedDeliveryException(i, new InvalidOperationException("boom"));
                delivered.Add(e.Path);
            }
            return Task.CompletedTask;
        }, () => delivered.Contains("good"), new WincheFeedOptions { DeadLetterAfter = TimeSpan.Zero });

        Assert.Equal(["good"], delivered);   // the dead letter didn't hold the next one back
        var letter = Assert.Single(await feed.GetDeadLettersAsync("hook"));
        Assert.Equal("bad", feed.RecordOf(letter).Path);
        Assert.Contains("boom", letter.Error);
        Assert.Equal(1, Assert.Single(await feed.GetStatusAsync(["hook"])).DeadLetters);

        var replay = await feed.ReplayDeadLettersAsync("hook", (e, _) => { delivered.Add(e.Path); return Task.CompletedTask; });
        Assert.Equal(new FeedReplayResult(1, 0), replay);
        Assert.Empty(await feed.GetDeadLettersAsync("hook"));
        Assert.Equal(["good", "bad"], delivered);
    }

    [Fact]
    public async Task Pruning_keeps_what_a_consumer_still_needs()
    {
        var (db, feed) = await StartAsync(await pg.NewDatabaseAsync());
        await feed.SavePositionAsync("slow", await feed.GetHeadAsync());
        await AppendAsync(db, "created", "a");
        var future = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.Equal(0, await feed.PruneAsync(future, DateTimeOffset.MinValue, ["slow"]));   // "slow" hasn't read it
        await feed.SavePositionAsync("slow", await feed.GetHeadAsync());
        Assert.Equal(1, await feed.PruneAsync(future, DateTimeOffset.MinValue, ["slow"]));
    }
}
