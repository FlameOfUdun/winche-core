namespace Winche;

/// <summary>
/// Thrown by an app's own code (a before hook, a sign-up check) to refuse a request with a sentence for the caller.
/// Every Winche package answers it as <see cref="WincheErrorCode.PermissionDenied"/> with this message and
/// <see cref="WincheError.Reason"/> <c>denied</c>, and nothing the request would have changed is saved.
/// </summary>
public class WincheDeniedException(string message) : Exception(message)
{
    /// <summary>The <see cref="WincheError.Reason"/> it's answered with.</summary>
    public const string Reason = "denied";

    /// <summary>The error the caller gets.</summary>
    public WincheError Error => new(WincheErrorCode.PermissionDenied, Message, Reason);
}
