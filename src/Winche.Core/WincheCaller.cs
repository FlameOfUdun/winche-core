namespace Winche;

/// <summary>
/// Who is calling: the verified claims of the request being handled (<c>uid</c>, <c>sub</c>, custom claims), or null
/// when it is signed out. One per request, shared by every Winche package — the database's and storage's rules, request
/// logs and history, and functions' <c>ctx.auth</c> all see the same caller.
/// <para>
/// The host sets it once per request (<c>AddWincheCaller</c> and <c>UseWincheCaller</c> in Winche.Core.AspNetCore); it
/// flows with the request's async work, a WebSocket connection's included.
/// </para>
/// </summary>
public static class WincheCaller
{
    private static readonly AsyncLocal<Holder?> Current = new();

    private sealed record Holder(IReadOnlyDictionary<string, object?>? Claims);

    /// <summary>The caller's claims, or null when signed out (or outside a request).</summary>
    public static IReadOnlyDictionary<string, object?>? Claims => Current.Value?.Claims;

    /// <summary>Whether a caller has been set for the current flow (signed out counts).</summary>
    public static bool IsSet => Current.Value is not null;

    /// <summary>The caller's user id: <c>uid</c>, else <c>sub</c>; null when signed out.</summary>
    public static string? Uid => UidOf(Claims);

    /// <summary>The user id in <paramref name="claims"/>: <c>uid</c>, else <c>sub</c>.</summary>
    public static string? UidOf(IReadOnlyDictionary<string, object?>? claims) =>
        claims is null ? null
        : claims.TryGetValue("uid", out var uid) && uid is not null ? uid.ToString()
        : claims.TryGetValue("sub", out var sub) && sub is not null ? sub.ToString()
        : null;

    /// <summary>Makes <paramref name="claims"/> the caller for the current flow and what it starts.</summary>
    public static void Set(IReadOnlyDictionary<string, object?>? claims) => Current.Value = new Holder(claims);

    /// <summary>Makes <paramref name="claims"/> the caller until the result is disposed, then restores the previous one.</summary>
    public static IDisposable Use(IReadOnlyDictionary<string, object?>? claims)
    {
        var previous = Current.Value;
        Current.Value = new Holder(claims);
        return new Restore(previous);
    }

    private sealed class Restore(Holder? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
