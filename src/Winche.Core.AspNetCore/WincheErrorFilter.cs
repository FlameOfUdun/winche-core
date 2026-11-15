using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Winche;

/// <summary>Sending a <see cref="WincheError"/>.</summary>
public static class WincheErrorResults
{
    /// <summary>The answer for <paramref name="error"/>: <c>{ "error": … }</c> with its HTTP status.</summary>
    public static IResult ToResult(this WincheError error) =>
        Results.Json(error.ToBody(), statusCode: error.HttpStatus, contentType: "application/json");
}

/// <summary>
/// An endpoint filter that answers a failure with the shared Winche error: a package supplies <see cref="Map"/> for the
/// exceptions it expects; a malformed body is <c>invalid-argument</c>; anything else is logged under an id
/// (<see cref="WincheError.Unexpected"/>) and answered <c>internal</c>.
/// </summary>
public abstract class WincheErrorFilter(ILogger logger) : IEndpointFilter
{
    /// <summary>The error for an exception this package expects, or null for an unexpected one.</summary>
    protected abstract WincheError? Map(Exception exception);

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var http = context.HttpContext;
            var error = (ex is BadHttpRequestException bad ? new WincheError(WincheErrorCode.InvalidArgument, bad.Message) : Map(ex))
                ?? WincheError.Unexpected(ex, logger, $"{http.Request.Method} {http.Request.Path}", WincheCaller.UidOf(await http.WincheCallerOf()));
            return error.ToResult();
        }
    }
}
