using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Winche.Core;

namespace Winche.Core.Tests;

public class ProductSetupTests
{
    private sealed class Work : IHostedService
    {
        public bool Started { get; private set; }
        public Task StartAsync(CancellationToken ct) { Started = true; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task A_products_background_work_starts_at_once_unless_products_are_set_up_on_demand()
    {
        var plain = new ServiceCollection();
        var work = new Work();
        plain.AddWincheProductService("database", _ => work);
        await using (var sp = plain.BuildServiceProvider())
        {
            foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(default);
            Assert.True(work.Started);
        }

        var onDemand = new ServiceCollection().AddWincheProductSetupOnDemand();
        var waiting = new Work();
        onDemand.AddWincheProductService("database", _ => waiting);
        await using var sp2 = onDemand.BuildServiceProvider();
        var hosted = sp2.GetServices<IHostedService>().ToList();
        foreach (var h in hosted) await h.StartAsync(default);
        await Task.Delay(50);
        Assert.False(waiting.Started);
        sp2.GetRequiredService<WincheProductReadiness>().MarkReady("database");
        for (var i = 0; i < 50 && !waiting.Started; i++) await Task.Delay(10);
        Assert.True(waiting.Started);
        foreach (var h in hosted) await h.StopAsync(default);
    }

    [Fact]
    public async Task Stopping_before_the_product_is_ready_never_starts_it()
    {
        var services = new ServiceCollection().AddWincheProductSetupOnDemand();
        var work = new Work();
        services.AddWincheProductService("storage", _ => work);
        await using var sp = services.BuildServiceProvider();
        var h = sp.GetServices<IHostedService>().Single();
        await h.StartAsync(default);
        await h.StopAsync(default);
        sp.GetRequiredService<WincheProductReadiness>().MarkReady("storage");
        await Task.Delay(30);
        Assert.False(work.Started);
    }
}
