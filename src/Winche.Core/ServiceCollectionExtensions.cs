using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Winche.Core;

/// <summary>Where the settings live, and how often other instances' changes are picked up.</summary>
public sealed class WincheConfigOptions
{
    /// <summary>The project's PostgreSQL database (table <c>winche_settings</c>; Winche.Secrets keeps its secrets here too).</summary>
    public string ConnectionString { get; set; } = "";

    /// <summary>
    /// The master key (32+ characters, from configuration or a key service): signing keys are derived from it
    /// (<see cref="WincheKeys"/>), and Winche.Secrets encrypts secrets with it.
    /// </summary>
    public string? MasterKey { get; set; }

    /// <summary>
    /// Master keys used before <see cref="MasterKey"/>, newest first, while changing it: what they encrypted is still
    /// read, and on start re-encrypted with the current key. Remove them once that's done.
    /// </summary>
    public IReadOnlyList<string> PreviousMasterKeys { get; set; } = [];

    /// <summary>How often each instance looks for changes saved elsewhere (default 5 seconds).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the settings store. Packages register their areas with <see cref="AddWincheSettingsArea{T}"/> (Winche.Auth's
    /// <c>UseSettings()</c>, …); without this call those areas simply use the code's values.
    /// </summary>
    public static IServiceCollection AddWincheConfig(this IServiceCollection services, Action<WincheConfigOptions> configure)
    {
        var options = new WincheConfigOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("Winche.Core: set ConnectionString.");
        if (options.MasterKey is { Length: < 32 }) throw new InvalidOperationException("Winche.Core: MasterKey must be at least 32 characters.");
        if (options.PreviousMasterKeys.Any(k => k is not { Length: >= 32 })) throw new InvalidOperationException("Winche.Core: previous master keys are at least 32 characters.");
        if (options.PreviousMasterKeys.Count > 0 && options.MasterKey is null) throw new InvalidOperationException("Winche.Core: previous master keys need a MasterKey to re-encrypt with.");
        services.AddSingleton(options);
        services.TryAddKeyedSingleton(nameof(WincheConfigOptions), (_, _) => NpgsqlDataSource.Create(options.ConnectionString));
        services.AddSingleton(sp => new SettingsStore(
            sp.GetRequiredKeyedService<NpgsqlDataSource>(nameof(WincheConfigOptions)),
            sp.GetServices<SettingsArea>(), sp.GetRequiredService<ILogger<SettingsStore>>()));
        services.AddSingleton(new WincheKeys(options.MasterKey, options.PreviousMasterKeys));
        services.AddHostedService<SettingsWatcher>();
        return services;
    }

    /// <summary>
    /// Registers an area of settings the console can change, with the code's values as its starting point. Resolve
    /// <see cref="SettingsArea{T}"/> to read <c>Current</c>. Works with or without <see cref="AddWincheConfig"/>: without
    /// it, the code's values always apply.
    /// </summary>
    public static SettingsArea<T> AddWincheSettingsArea<T>(this IServiceCollection services, string name, string title, T codeDefault,
        Func<T, IReadOnlyList<string>>? validate = null) where T : class
    {
        if (services.Any(d => d.ServiceType == typeof(SettingsArea) && d.ImplementationInstance is SettingsArea a && a.Name == name))
            throw new InvalidOperationException($"The settings area '{name}' is registered twice.");
        var area = new SettingsArea<T>(name, title, codeDefault, validate);
        services.AddSingleton<SettingsArea>(area);
        services.AddSingleton(area);
        return area;
    }
}

/// <summary>Makes the tables and loads the settings on start, then picks up other instances' changes.</summary>
internal sealed class SettingsWatcher(SettingsStore store, WincheConfigOptions options, ILogger<SettingsWatcher> logger) : BackgroundService
{
    public override async Task StartAsync(CancellationToken ct)
    {
        await store.InitializeAsync(ct);
        await base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try { await store.RefreshAsync(force: false, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't check for settings changes; trying again in {Interval}.", options.PollInterval);
            }
        }
    }
}
