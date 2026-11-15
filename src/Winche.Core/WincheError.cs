using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Winche;

/// <summary>
/// What kind of failure an error is, the same in every Winche API and SDK. On the wire it is the kebab-case name
/// (<c>not-found</c>, <c>permission-denied</c>, …).
/// </summary>
public enum WincheErrorCode
{
    Cancelled,
    Unknown,
    InvalidArgument,
    DeadlineExceeded,
    NotFound,
    AlreadyExists,
    PermissionDenied,
    ResourceExhausted,
    FailedPrecondition,
    Aborted,
    OutOfRange,
    Unimplemented,
    Internal,
    Unavailable,
    DataLoss,
    Unauthenticated,
}

/// <summary>
/// The error every Winche API answers with: <c>{ "error": { "code", "message", "reason"?, "details"? } }</c>.
/// <c>code</c> is a <see cref="WincheErrorCode"/>; <c>reason</c> is the API's own, more precise code where callers need
/// it (<c>INVALID_CREDENTIALS</c>, <c>invalid-query</c>, a function's own code); <c>details</c> is anything else.
/// </summary>
public sealed record WincheError(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("reason"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Reason = null,
    [property: JsonPropertyName("details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonObject? Details = null)
{
    /// <summary>An error of kind <paramref name="code"/>.</summary>
    public WincheError(WincheErrorCode code, string message, string? reason = null, JsonObject? details = null)
        : this(Name(code), message, reason, details) { }

    /// <summary>The kind, read back from <see cref="Code"/> (<see cref="WincheErrorCode.Unknown"/> if it isn't one).</summary>
    [JsonIgnore] public WincheErrorCode Kind => Parse(Code);

    /// <summary>The HTTP status an answer with this error carries.</summary>
    [JsonIgnore] public int HttpStatus => StatusOf(Kind);

    /// <summary>The body to send: <c>{ "error": this }</c>.</summary>
    public WincheErrorBody ToBody() => new(this);

    /// <summary>
    /// The error for an exception nobody expected: logged at Error level with <paramref name="operation"/> and
    /// <paramref name="user"/> under a short id, which the caller gets in the message and in <c>details.errorId</c> — so
    /// a report like "Internal error (3f9c0a1b2c4d)" leads to the log entry, and nothing else about it leaves the server.
    /// </summary>
    public static WincheError Unexpected(Exception exception, ILogger logger, string operation, string? user)
    {
        var errorId = Guid.NewGuid().ToString("N")[..12];
        logger.LogError(exception, "Internal error {ErrorId} in {Operation} for {User}", errorId, operation, user ?? "(signed out)");
        return new WincheError(WincheErrorCode.Internal, $"Internal error ({errorId}).", details: new JsonObject { ["errorId"] = errorId });
    }

    /// <summary>The wire name of <paramref name="code"/>: <c>NotFound</c> → <c>not-found</c>.</summary>
    public static string Name(WincheErrorCode code) => code switch
    {
        WincheErrorCode.Cancelled => "cancelled",
        WincheErrorCode.Unknown => "unknown",
        WincheErrorCode.InvalidArgument => "invalid-argument",
        WincheErrorCode.DeadlineExceeded => "deadline-exceeded",
        WincheErrorCode.NotFound => "not-found",
        WincheErrorCode.AlreadyExists => "already-exists",
        WincheErrorCode.PermissionDenied => "permission-denied",
        WincheErrorCode.ResourceExhausted => "resource-exhausted",
        WincheErrorCode.FailedPrecondition => "failed-precondition",
        WincheErrorCode.Aborted => "aborted",
        WincheErrorCode.OutOfRange => "out-of-range",
        WincheErrorCode.Unimplemented => "unimplemented",
        WincheErrorCode.Internal => "internal",
        WincheErrorCode.Unavailable => "unavailable",
        WincheErrorCode.DataLoss => "data-loss",
        WincheErrorCode.Unauthenticated => "unauthenticated",
        _ => "unknown",
    };

    /// <summary>The kind named <paramref name="name"/>, or <see cref="WincheErrorCode.Unknown"/>.</summary>
    public static WincheErrorCode Parse(string? name) =>
        Enum.GetValues<WincheErrorCode>().FirstOrDefault(c => Name(c) == name, WincheErrorCode.Unknown);

    /// <summary>Whether <paramref name="name"/> is one of the codes.</summary>
    public static bool IsCode(string? name) => Enum.GetValues<WincheErrorCode>().Any(c => Name(c) == name);

    /// <summary>The HTTP status for each kind, the same in every Winche API.</summary>
    public static int StatusOf(WincheErrorCode code) => code switch
    {
        WincheErrorCode.InvalidArgument or WincheErrorCode.OutOfRange or WincheErrorCode.FailedPrecondition => 400,
        WincheErrorCode.Unauthenticated => 401,
        WincheErrorCode.PermissionDenied => 403,
        WincheErrorCode.NotFound => 404,
        WincheErrorCode.AlreadyExists or WincheErrorCode.Aborted => 409,
        WincheErrorCode.ResourceExhausted => 429,
        WincheErrorCode.Cancelled => 499,
        WincheErrorCode.Unimplemented => 501,
        WincheErrorCode.Unavailable => 503,
        WincheErrorCode.DeadlineExceeded => 504,
        _ => 500,   // Internal, Unknown, DataLoss
    };
}

/// <summary><c>{ "error": … }</c>: what a Winche API sends when a request fails.</summary>
public sealed record WincheErrorBody([property: JsonPropertyName("error")] WincheError Error);
