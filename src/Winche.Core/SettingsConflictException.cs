namespace Winche.Core;

/// <summary>
/// A save made from a version that's no longer the latest: someone saved since, and saving would undo their change.
/// </summary>
public sealed class SettingsConflictException(string area, int latest)
    : InvalidOperationException($"Someone else changed these settings (now version {latest}) since you opened them. Reload to see theirs, then make your change again.")
{
    public string Area { get; } = area;
    public int Latest { get; } = latest;
}
