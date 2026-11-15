using System.Text.Json.Nodes;

namespace Winche;

/// <summary>
/// Something that changed in a Winche service — a document written, a file uploaded, a user signed in — as hooks and
/// functions see it. Database, storage and auth publish them to every <see cref="IWincheChangeSink"/>.
/// </summary>
/// <param name="Source">database, storage, auth, or another package's own.</param>
/// <param name="Type">e.g. created, updated, deleted, uploaded, signedIn.</param>
/// <param name="Path">The document or file path (null for auth events).</param>
/// <param name="Data">The thing as it is now (plain JSON), when there is one.</param>
/// <param name="Before">As it was, when known (updates and deletes).</param>
/// <param name="Variables">Values actions can use: uid, email, path, id, … (path-pattern captures are added per hook).</param>
public sealed record WincheChange(string Source, string Type, string? Path, DateTimeOffset At, JsonObject? Data, JsonObject? Before,
    IReadOnlyDictionary<string, string> Variables)
{
    /// <summary>Unique per change: receivers can use it to ignore repeats (deliveries are at least once).</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

/// <summary>A kind of change a package publishes, for the console's Hooks page and for checking hooks.</summary>
/// <param name="HasPaths">Its changes have paths that hooks can match.</param>
/// <param name="Variables">The variables every one of its changes carries.</param>
/// <param name="Internal">Used by other packages (e.g. function schedules), not offered in the console's Hooks page.</param>
public sealed record WincheChangeSource(string Name, string Title, IReadOnlyList<string> Events, bool HasPaths, IReadOnlyList<string> Variables,
    bool Internal = false);

/// <summary>
/// Receives the changes Winche services publish (Winche.Webhooks registers one). A sink must not throw for one bad
/// receiver: database and storage deliver at least once and retry the batch when it does.
/// </summary>
public interface IWincheChangeSink
{
    /// <summary>Takes <paramref name="changes"/>; returning means they're safely queued.</summary>
    Task OnChangesAsync(IReadOnlyList<WincheChange> changes, CancellationToken ct);
}

/// <summary>Publishing helpers for packages.</summary>
public static class WincheChanges
{
    /// <summary>Hands <paramref name="change"/> to every sink, in order.</summary>
    public static async Task PublishAsync(IEnumerable<IWincheChangeSink> sinks, WincheChange change, CancellationToken ct)
    {
        foreach (var sink in sinks) await sink.OnChangesAsync([change], ct);
    }
}

/// <summary>An option of a ready-made action.</summary>
public sealed record WincheActionOption(string Name, string Label, string? Placeholder = null, bool Required = true, string? Help = null);

/// <summary>What an action gets: the change, its options with <c>{name}</c> filled in, and the app's services (scoped).</summary>
public sealed record WincheActionContext(WincheChange Change, IReadOnlyDictionary<string, string> Options, IServiceProvider Services);

/// <summary>
/// A ready-made action the console can attach to changes, e.g. "delete files under …". A package registers its own
/// (database, storage, auth); Winche.Webhooks runs them from its delivery queue, retrying on failure, so one should be
/// safe to run twice.
/// </summary>
public interface IWincheAction
{
    /// <summary>e.g. storage.deleteFiles.</summary>
    string Name { get; }
    string Title { get; }
    string Description { get; }
    /// <summary>The sources whose changes it suits; any when empty.</summary>
    IReadOnlyList<string> Sources { get; }
    IReadOnlyList<WincheActionOption> Options { get; }
    /// <summary>Runs it; returns a sentence about what it did (shown in the delivery log).</summary>
    Task<string> RunAsync(WincheActionContext context, CancellationToken ct);
}
