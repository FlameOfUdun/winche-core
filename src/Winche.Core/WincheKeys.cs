using System.Security.Cryptography;
using System.Text;

namespace Winche.Core;

/// <summary>
/// Keys derived from the project's master key (<see cref="WincheConfigOptions.MasterKey"/>), one per purpose, so one
/// secret to keep gives every package what it needs: Winche Auth's signing-key encryption, signed file links, …
/// A package asks for its key by purpose when its own option isn't set.
/// </summary>
public sealed class WincheKeys(string? masterKey, IReadOnlyList<string>? previousKeys = null)
{
    /// <summary>
    /// Master keys used before this one (<see cref="WincheConfigOptions.PreviousMasterKeys"/>): what they encrypted is
    /// still read, and re-encrypted with the current key.
    /// </summary>
    public IReadOnlyList<string> PreviousKeys { get; } = [.. (previousKeys ?? []).Where(k => k is { Length: >= 32 })];

    /// <summary>The key for <paramref name="purpose"/> from each previous master key, newest first.</summary>
    public IReadOnlyList<string> DerivePrevious(string purpose) => [.. PreviousKeys.Select(k => DeriveWith(k, purpose))];

    private static string DeriveWith(string key, string purpose) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes($"winche-server:{purpose}")));

    /// <summary>Whether a master key is set.</summary>
    public bool Available => masterKey is { Length: >= 32 };

    /// <summary>
    /// The key for <paramref name="purpose"/> (e.g. <c>file-links</c>): 64 hex characters, the same on every server
    /// with the same master key. Unchanged from Winche Server's earlier derivation, so existing keys and links stay valid.
    /// </summary>
    public string Derive(string purpose)
    {
        var key = Available ? masterKey! : throw new InvalidOperationException(
            $"A key for '{purpose}' needs a master key: set WincheConfigOptions.MasterKey (32+ characters, from configuration).");
        return DeriveWith(key, purpose);
    }

    /// <summary>The key for <paramref name="purpose"/>, or null when no master key is set.</summary>
    public string? TryDerive(string purpose) => Available ? Derive(purpose) : null;
}
