using System.Text.RegularExpressions;

namespace Winche;

/// <summary>A segment of a <see cref="PathPattern"/>.</summary>
public abstract record PathPatternSegment;

/// <summary>A segment that matches itself.</summary>
public sealed record LiteralPathSegment(string Value) : PathPatternSegment;

/// <summary><c>{name}</c>: one segment, captured as <c>name</c>.</summary>
public sealed record ParamPathSegment(string Name) : PathPatternSegment;

/// <summary><c>{name=**}</c>: the segments it spans, captured as a path.</summary>
public sealed record RecursivePathSegment(string Name) : PathPatternSegment;

/// <summary>
/// A path pattern, the same everywhere in Winche — rules' <c>match</c>, hooks' and functions' trigger paths:
/// literal segments, <c>{id}</c> for one segment, and <c>{name=**}</c> for several. A recursive capture at the end
/// (<c>users/{rest=**}</c>) matches one or more segments; one at the start or in the middle
/// (<c>{path=**}/posts/{postId}</c>) matches zero or more.
/// </summary>
public sealed partial class PathPattern
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex CaptureName();

    private PathPattern(IReadOnlyList<PathPatternSegment> segments) => Segments = segments;

    /// <summary>Its segments.</summary>
    public IReadOnlyList<PathPatternSegment> Segments { get; }

    /// <summary>The names it captures, in order.</summary>
    public IReadOnlyList<string> Names => [.. Segments.Select(s => s switch { ParamPathSegment p => p.Name, RecursivePathSegment r => r.Name, _ => null }).OfType<string>()];

    /// <summary>How many recursive captures it has (a pattern may have one).</summary>
    public int RecursiveCount => Segments.Count(s => s is RecursivePathSegment);

    /// <summary>A pattern of <paramref name="segments"/>, e.g. a nested rule's path joined onto its parent's.</summary>
    public static PathPattern Of(IEnumerable<PathPatternSegment> segments) => new([.. segments]);

    /// <summary>Parses <paramref name="text"/>; throws an <see cref="ArgumentException"/> saying what's wrong.</summary>
    public static PathPattern Parse(string text) =>
        TryParse(text, out var pattern, out var error) ? pattern! : throw new ArgumentException(error, nameof(text));

    /// <summary>Parses <paramref name="text"/>, or says what's wrong with it.</summary>
    public static bool TryParse(string? text, out PathPattern? pattern, out string? error)
    {
        pattern = null;
        error = null;
        var raw = (text ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (raw.Length == 0) { error = "A path pattern needs at least one segment."; return false; }
        var segments = new List<PathPatternSegment>(raw.Length);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in raw)
        {
            if (s.Contains('{') || s.Contains('}'))
            {
                if (s[0] != '{' || s[^1] != '}' || s.IndexOf('{', 1) >= 0 || s.LastIndexOf('}', s.Length - 2) >= 0)
                {
                    error = $"'{s}' mixes text and a capture: '{{' and '}}' must wrap the whole segment, like {{id}}.";
                    return false;
                }
                var inner = s[1..^1];
                var recursive = inner.EndsWith("=**", StringComparison.Ordinal);
                var name = recursive ? inner[..^3] : inner;
                if (!recursive && inner.Contains('='))
                {
                    error = $"'{s}' isn't valid: only the recursive {{name=**}} form may use '='.";
                    return false;
                }
                if (!CaptureName().IsMatch(name))
                {
                    error = $"'{s}' isn't a valid capture: a name is letters, digits and _, not starting with a digit.";
                    return false;
                }
                if (!names.Add(name)) { error = $"'{name}' is captured twice."; return false; }
                if (recursive && segments.Any(x => x is RecursivePathSegment))
                {
                    error = $"A path may have only one recursive capture; '{s}' is a second.";
                    return false;
                }
                segments.Add(recursive ? new RecursivePathSegment(name) : new ParamPathSegment(name));
            }
            else if (s.Contains('*'))
            {
                error = $"'{s}' isn't valid: use {{name}} for one segment or {{name=**}} for several.";
                return false;
            }
            else segments.Add(new LiteralPathSegment(s));
        }
        pattern = new PathPattern(segments);
        return true;
    }

    /// <summary>The captures when <paramref name="path"/> matches, else null.</summary>
    public IReadOnlyDictionary<string, string>? Match(string path) => TryMatch(path, out var captures) ? captures : null;

    /// <summary>Whether <paramref name="path"/> matches, and its captures.</summary>
    public bool TryMatch(string path, out Dictionary<string, string> captures)
    {
        captures = new(StringComparer.Ordinal);
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var recursive = -1;
        for (var i = 0; i < Segments.Count; i++)
        {
            if (Segments[i] is not RecursivePathSegment) continue;
            if (recursive >= 0) return false;
            recursive = i;
        }

        if (recursive < 0)
            return Segments.Count == segs.Length && MatchRange(0, segs, 0, Segments.Count, captures);

        var suffix = Segments.Count - recursive - 1;
        var middle = segs.Length - recursive - suffix;
        if (middle < (suffix == 0 ? 1 : 0)) return false;   // trailing: one or more; otherwise zero or more
        if (!MatchRange(0, segs, 0, recursive, captures)) return false;
        if (!MatchRange(recursive + 1, segs, segs.Length - suffix, suffix, captures)) return false;
        captures[((RecursivePathSegment)Segments[recursive]).Name] = string.Join('/', segs[recursive..(recursive + middle)]);
        return true;
    }

    /// <summary>
    /// Whether <paramref name="path"/> starts with this pattern (which must have no recursive capture), and its captures:
    /// e.g. whether a query on a collection falls under a rule's prefix.
    /// </summary>
    public bool TryMatchPrefix(string path, out Dictionary<string, string> captures)
    {
        captures = new(StringComparer.Ordinal);
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length < Segments.Count || RecursiveCount > 0) return false;
        return MatchRange(0, segs, 0, Segments.Count, captures);
    }

    private bool MatchRange(int from, string[] segs, int segFrom, int count, Dictionary<string, string> captures)
    {
        for (var j = 0; j < count; j++)
        {
            var seg = segs[segFrom + j];
            switch (Segments[from + j])
            {
                case LiteralPathSegment lit when !string.Equals(lit.Value, seg, StringComparison.Ordinal):
                    return false;
                case ParamPathSegment p:
                    captures[p.Name] = seg;
                    break;
            }
        }
        return true;
    }

    /// <inheritdoc />
    public override string ToString() => string.Join('/', Segments.Select(s => s switch
    {
        LiteralPathSegment l => l.Value, ParamPathSegment p => $"{{{p.Name}}}", RecursivePathSegment r => $"{{{r.Name}=**}}", _ => "?",
    }));
}
