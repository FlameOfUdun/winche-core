namespace Winche.Core.Feeds;

/// <summary>
/// A package's durable consumers of its feed, for the console and health checks: how they're doing, the records they
/// gave up on, and retrying or discarding those. Packages register one each (database: its hooks and published
/// changes; storage: its file hooks); the console shows every one registered.
/// </summary>
public interface IWincheFeedConsumers
{
    /// <summary>The feed's name (<c>database</c>, <c>storage</c>).</summary>
    string Feed { get; }

    Task<IReadOnlyList<FeedConsumerStatus>> ConsumersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<FeedDeadLetter>> DeadLettersAsync(string? consumer = null, int limit = 100, CancellationToken ct = default);

    /// <summary>Delivers <paramref name="consumer"/>'s dead letters again; those that fail again are kept.</summary>
    Task<FeedReplayResult> RetryAsync(string consumer, CancellationToken ct = default);

    /// <summary>Drops one dead letter without delivering it; false when there's none with that id.</summary>
    Task<bool> DiscardAsync(long id, CancellationToken ct = default);
}
