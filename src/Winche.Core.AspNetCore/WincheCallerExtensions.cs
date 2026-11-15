using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Winche;

/// <summary>How the host reads the caller's claims from a request: one mapping, for every Winche package.</summary>
public sealed class WincheCallerMap(Func<HttpContext, ValueTask<IReadOnlyDictionary<string, object?>?>> map)
{
    /// <summary>The claims of <paramref name="http"/>'s caller, or null when it is signed out.</summary>
    public ValueTask<IReadOnlyDictionary<string, object?>?> ClaimsOf(HttpContext http) => map(http);
}

/// <summary>Setting <see cref="WincheCaller"/> for each request.</summary>
public static class WincheCallerExtensions
{
    private const string Item = "winche.caller";

    /// <summary>
    /// How to read the caller's verified claims from a request (e.g. <c>WincheClaims.FromAsync</c> for Winche Auth's
    /// tokens): every Winche package — database and storage rules, request logs, functions' <c>ctx.auth</c> — uses it.
    /// Pair with <see cref="UseWincheCaller"/>.
    /// </summary>
    public static IServiceCollection AddWincheCaller(this IServiceCollection services,
        Func<HttpContext, ValueTask<IReadOnlyDictionary<string, object?>?>> claims)
    {
        services.AddSingleton(new WincheCallerMap(claims));
        return services;
    }

    /// <summary><see cref="AddWincheCaller(IServiceCollection, Func{HttpContext, ValueTask{IReadOnlyDictionary{string, object}}})"/> with a synchronous mapping.</summary>
    public static IServiceCollection AddWincheCaller(this IServiceCollection services, Func<HttpContext, IReadOnlyDictionary<string, object?>?> claims) =>
        services.AddWincheCaller(http => ValueTask.FromResult(claims(http)));

    /// <summary>
    /// Sets <see cref="WincheCaller"/> for each request, from the mapping given to <c>AddWincheCaller</c>. Place it after
    /// <c>UseAuthentication</c> and before the Winche endpoints; a WebSocket connection keeps its upgrade request's caller.
    /// </summary>
    public static IApplicationBuilder UseWincheCaller(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var map = http.RequestServices.GetService<WincheCallerMap>()
            ?? throw new InvalidOperationException("UseWincheCaller needs AddWincheCaller(…): say how to read the caller's claims.");
        var claims = await map.ClaimsOf(http);
        http.Items[Item] = claims;
        using (WincheCaller.Use(claims)) await next(http);
    });

    /// <summary>
    /// The caller of <paramref name="http"/>: what <see cref="UseWincheCaller"/> set, else the <c>AddWincheCaller</c>
    /// mapping, else <paramref name="fallback"/> (a package's own mapping); null when signed out.
    /// </summary>
    public static async ValueTask<IReadOnlyDictionary<string, object?>?> WincheCallerOf(this HttpContext http,
        Func<HttpContext, IReadOnlyDictionary<string, object?>?>? fallback = null)
    {
        if (fallback is not null) return fallback(http);
        if (http.Items.TryGetValue(Item, out var set)) return set as IReadOnlyDictionary<string, object?>;
        if (WincheCaller.IsSet) return WincheCaller.Claims;
        return http.RequestServices.GetService<WincheCallerMap>() is { } map ? await map.ClaimsOf(http) : null;
    }
}
