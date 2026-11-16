using System.Text.Json.Nodes;

namespace Winche;

/// <summary>Who a reference is resolved for: a caller's claims (rules apply), or the app's own code (<see cref="Trusted"/>).</summary>
public sealed record ReferenceCaller(IReadOnlyDictionary<string, object?>? Claims, bool Trusted)
{
    /// <summary>The app's own server code: no rules.</summary>
    public static readonly ReferenceCaller Server = new(null, true);

    /// <summary>The caller's uid, when signed in.</summary>
    public string? Uid => Claims?.GetValueOrDefault("uid") as string;
}

/// <summary>
/// Turns a file or user reference a document holds (<c>fileReferenceValue</c>, <c>userReferenceValue</c>) into what it stands
/// for, so the database's <c>expand</c> can return it: <c>expand: ["scan", "author"]</c>. Winche.Storage registers
/// <c>file</c> and Winche Auth <c>user</c>. Registered in DI, one per <see cref="Kind"/>.
/// </summary>
public interface IWincheReferenceResolver
{
    /// <summary>What it resolves: <c>file</c> (file references) or <c>user</c> (user references).</summary>
    string Kind { get; }

    /// <summary>
    /// What each of <paramref name="keys"/> stands for, as <paramref name="caller"/> may see it; keys that don't exist or
    /// that the caller may not see are left out (the reference comes back without data).
    /// </summary>
    Task<IReadOnlyDictionary<string, JsonObject>> ResolveAsync(IReadOnlyList<string> keys, ReferenceCaller caller, CancellationToken ct);

    /// <summary>
    /// A Postgres channel notified when something this resolves may have changed, so live subscriptions resolve again;
    /// null when it can't tell (resolved values are then refreshed only with the results).
    /// </summary>
    string? ChangeChannel { get; }
}
