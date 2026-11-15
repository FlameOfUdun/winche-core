using System.Security.Cryptography;
using System.Text;

namespace Winche;

/// <summary>
/// Base64url as Winche uses it: document and file paths in URLs, token payloads, random keys. Decoding accepts the
/// url alphabet (<c>-</c>, <c>_</c>) and the standard one (<c>+</c>, <c>/</c>), padded or not.
/// </summary>
public static class WincheBase64Url
{
    /// <summary>The base64url of <paramref name="bytes"/>, unpadded.</summary>
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The base64url of <paramref name="text"/>'s UTF-8, unpadded.</summary>
    public static string Encode(string text) => Encode(Encoding.UTF8.GetBytes(text));

    /// <summary>The bytes in <paramref name="text"/>; throws a <see cref="FormatException"/> when it isn't base64.</summary>
    public static byte[] DecodeBytes(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        if (s.Length % 4 == 1) throw new FormatException($"'{text}' isn't base64url.");
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { throw new FormatException($"'{text}' isn't base64url."); }
    }

    /// <summary>The UTF-8 text in <paramref name="text"/>; throws a <see cref="FormatException"/> when it isn't base64.</summary>
    public static string Decode(string text) => Encoding.UTF8.GetString(DecodeBytes(text));

    /// <summary>The UTF-8 text in <paramref name="text"/>, or false when it isn't base64.</summary>
    public static bool TryDecode(string text, out string? decoded)
    {
        try { decoded = Decode(text); return true; }
        catch (FormatException) { decoded = null; return false; }
    }

    /// <summary><paramref name="bytes"/> random bytes as base64url: for keys and secrets.</summary>
    public static string Random(int bytes = 24) => Encode(RandomNumberGenerator.GetBytes(bytes));
}
