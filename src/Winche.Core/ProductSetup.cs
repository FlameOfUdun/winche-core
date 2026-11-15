using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Winche.Core;

/// <summary>The products' names, as setups and readiness know them.</summary>
public static class WincheProductNames
{
    public const string Database = "database", Storage = "storage", Authentication = "authentication", Functions = "functions", Hooks = "hooks";
}

/// <summary>
/// What a product does to be ready in a database: make its tables, functions and indexes, and anything else it needs
/// first (Winche Auth's signing key). In named steps, so a console can show them as they go. Every step is safe to
/// run again.
/// </summary>
public interface IWincheProductSetup
{
    /// <summary>The product it sets up (<see cref="WincheProductNames"/>).</summary>
    string Product { get; }

    /// <summary>The steps' titles, in order ("Create the user tables").</summary>
    IReadOnlyList<string> Steps { get; }

    Task RunStepAsync(int step, CancellationToken ct);
}

/// <summary>
/// Products set up on demand (Winche Server): with this registered, a product makes nothing in its database when the
/// app starts, and its background work waits until its setup has run (<see cref="MarkReady"/>). Without it, products
/// set themselves up as the app starts, as a .NET app using them directly expects.
/// </summary>
public sealed class WincheProductReadiness
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _ready = new();

    private TaskCompletionSource For(string product) =>
        _ready.GetOrAdd(product, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    public bool IsReady(string product) => For(product).Task.IsCompleted;

    /// <summary>Waits until <paramref name="product"/> is set up.</summary>
    public Task WaitAsync(string product, CancellationToken ct) => For(product).Task.WaitAsync(ct);

    public void MarkReady(string product) => For(product).TrySetResult();
}

public static class WincheProductSetupExtensions
{
    /// <summary>Products are set up on demand (see <see cref="WincheProductReadiness"/>), not as the app starts.</summary>
    public static IServiceCollection AddWincheProductSetupOnDemand(this IServiceCollection services)
    {
        services.AddSingleton<WincheProductReadiness>();
        return services;
    }

    /// <summary>Whether products are set up on demand here (then a product doesn't set itself up as the app starts).</summary>
    public static bool SetsUpProductsOnDemand(this IServiceProvider services) => services.GetService<WincheProductReadiness>() is not null;

    /// <summary>For a product's background work: waits until it's set up (at once when products set themselves up).</summary>
    public static Task WhenProductReadyAsync(this IServiceProvider services, string product, CancellationToken ct) =>
        services.GetService<WincheProductReadiness>() is { } readiness ? readiness.WaitAsync(product, ct) : Task.CompletedTask;

    /// <summary>Runs every step of <paramref name="setup"/>.</summary>
    public static async Task RunAllAsync(this IWincheProductSetup setup, CancellationToken ct = default)
    {
        for (var i = 0; i < setup.Steps.Count; i++) await setup.RunStepAsync(i, ct);
    }
}

/// <summary>
/// A product's background work, started once the product is set up (at once when products set themselves up). Packages
/// register theirs with <see cref="WincheReadyServiceExtensions.AddWincheProductService{T}"/>.
/// </summary>
public sealed class WhenProductReadyService(IServiceProvider services, string product, Microsoft.Extensions.Hosting.IHostedService inner)
    : Microsoft.Extensions.Hosting.IHostedService
{
    private readonly CancellationTokenSource _stopping = new();
    private Task? _waiting;
    private volatile bool _started;

    /// <summary>The service it starts.</summary>
    public Microsoft.Extensions.Hosting.IHostedService Inner => inner;

    public Task StartAsync(CancellationToken ct)
    {
        if (services.GetService<WincheProductReadiness>() is not { } readiness)
        {
            _started = true;
            return inner.StartAsync(ct);
        }
        _waiting = Task.Run(async () =>
        {
            try
            {
                await readiness.WaitAsync(product, _stopping.Token);
                await inner.StartAsync(_stopping.Token);
                _started = true;
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        await _stopping.CancelAsync();
        if (_waiting is not null) await _waiting;
        if (_started) await inner.StopAsync(ct);
    }
}

/// <summary>Which background work belongs to which product (for checking a registration).</summary>
public sealed record WincheProductServiceInfo(string Product, Type Service);

public static class WincheReadyServiceExtensions
{
    /// <summary>Registers <typeparamref name="T"/> as <paramref name="product"/>'s background work (see <see cref="WhenProductReadyService"/>).</summary>
    public static IServiceCollection AddWincheProductService<T>(this IServiceCollection services, string product)
        where T : class, Microsoft.Extensions.Hosting.IHostedService
    {
        services.AddSingleton(new WincheProductServiceInfo(product, typeof(T)));
        return services.AddWincheProductService(product, sp => ActivatorUtilities.CreateInstance<T>(sp));
    }

    public static IServiceCollection AddWincheProductService(this IServiceCollection services, string product,
        Func<IServiceProvider, Microsoft.Extensions.Hosting.IHostedService> create)
    {
        // Add, not TryAddEnumerable: every one of these has the same implementation type.
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => new WhenProductReadyService(sp, product, create(sp)));
        return services;
    }
}
