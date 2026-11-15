namespace Winche;

/// <summary>The rule for the ids people choose: projects, apps.</summary>
public static class WincheIds
{
    /// <summary>3 to 40 lower-case letters, digits and dashes, starting with a letter and not ending with a dash.</summary>
    public static bool IsValid(string? id) =>
        id is { Length: >= 3 and <= 40 } && char.IsAsciiLetterLower(id[0]) && id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && !id.EndsWith('-');
}
