using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Winche.Core.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async Task InitializeAsync() => await _container.StartAsync();
    public async Task DisposeAsync() => await _container.DisposeAsync();

    public async Task<string> NewDatabaseAsync()
    {
        var name = $"c{Guid.NewGuid():N}"[..20];
        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE DATABASE {name}", conn);
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Database = name }.ConnectionString;
    }
}

[CollectionDefinition("postgres")]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>;

public sealed record Greeting(string Text, int Times);

[Collection("postgres")]
public class SettingsTests(PostgresFixture pg)
{
    private static async Task<(ServiceProvider Sp, SettingsArea<Greeting> Area, SettingsStore Store)> Start(string db, string? masterKey = null,
        TimeSpan? poll = null)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddWincheConfig(o => { o.ConnectionString = db; o.MasterKey = masterKey; o.PollInterval = poll ?? TimeSpan.FromMinutes(1); });
        var area = services.AddWincheSettingsArea("demo.greeting", "Greeting", new Greeting("hello", 1),
            g => g.Times < 1 ? ["Times must be at least 1."] : []);
        var sp = services.BuildServiceProvider();
        foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(default);
        return (sp, area, sp.GetRequiredService<SettingsStore>());
    }

    private static async Task<ServiceProvider> StartWithKeys(string db, string masterKey, params string[] previous)
    {
        var services = new ServiceCollection().AddLogging();
        services.AddWincheConfig(o => { o.ConnectionString = db; o.MasterKey = masterKey; o.PreviousMasterKeys = previous; });
        var sp = services.BuildServiceProvider();
        foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(default);
        return sp;
    }

    [Fact]
    public async Task A_changed_master_key_reads_secrets_with_the_previous_one_and_re_encrypts_them_on_start()
    {
        var db = await pg.NewDatabaseAsync();
        const string oldKey = "old-master-key-0123456789abcdefghijklmnop", newKey = "new-master-key-0123456789abcdefghijklmnop";
        await using (var before = await StartWithKeys(db, oldKey))
        {
            await before.GetRequiredService<SecretStore>().SetAsync("smtp.password", "pw-1", "admin@x");
            await before.GetRequiredService<SecretStore>().SetAsync("keycloak.clientSecret", "pw-2", "admin@x");
        }

        // The new key alone can't read them.
        await using (var alone = await StartWithKeys(db, newKey))
            await Assert.ThrowsAsync<InvalidOperationException>(() => alone.GetRequiredService<SecretStore>().GetAsync("smtp.password"));

        // With the old one as previous: re-encrypted on start, who set them kept.
        await using (var rotating = await StartWithKeys(db, newKey, oldKey))
        {
            var secrets = rotating.GetRequiredService<SecretStore>();
            Assert.Equal(new SecretKeyStatus(0, 2, 0), await secrets.ReencryptAsync());
            Assert.Equal("admin@x", (await secrets.InfoAsync("smtp.password"))?.SetBy);
        }

        // Now the new key alone reads everything.
        await using var after = await StartWithKeys(db, newKey);
        Assert.Equal("pw-1", await after.GetRequiredService<SecretStore>().GetAsync("smtp.password"));
        Assert.Equal("pw-2", await after.GetRequiredService<SecretStore>().GetAsync("keycloak.clientSecret"));
    }

    private static JsonNode Doc(string text, int times) => new JsonObject { ["text"] = text, ["times"] = times };

    [Fact]
    public async Task A_save_from_an_older_version_is_refused_instead_of_undoing_another()
    {
        var db = await pg.NewDatabaseAsync();
        var (sp, _, store) = await Start(db);
        await using (sp)
        {
            var v1 = await store.SaveAsync("demo.greeting", Doc("first", 1), "a", "A", basedOn: 0);
            // B opened version 1 too; A saves version 2; B's save from 1 is refused.
            await store.SaveAsync("demo.greeting", Doc("from A", 2), "a", "A again", basedOn: v1.Number);
            var conflict = await Assert.ThrowsAsync<SettingsConflictException>(() =>
                store.SaveAsync("demo.greeting", Doc("from B", 3), "b", "B", basedOn: v1.Number));
            Assert.Equal(2, conflict.Latest);
            Assert.Equal(3, (await store.SaveAsync("demo.greeting", Doc("from B", 3), "b", "B, after reloading", basedOn: 2)).Number);
            await store.SaveAsync("demo.greeting", Doc("as is", 4), "c", "No version given");   // without basedOn: as before
        }
    }

    [Fact]
    public async Task Saved_settings_can_be_loaded_before_the_watcher_starts()
    {
        var db = await pg.NewDatabaseAsync();
        var (first, _, store) = await Start(db);
        await using (first) await store.SaveAsync("demo.greeting", Doc("saved", 2), null, null);

        var services = new ServiceCollection().AddLogging();
        services.AddWincheConfig(o => o.ConnectionString = db);
        var area = services.AddWincheSettingsArea("demo.greeting", "Greeting", new Greeting("hello", 1));
        await using var sp = services.BuildServiceProvider();   // hosted services not started
        Assert.Equal("hello", area.Current.Text);
        var second = sp.GetRequiredService<SettingsStore>();
        await Task.WhenAll(second.EnsureLoadedAsync(), second.EnsureLoadedAsync());
        Assert.Equal("saved", area.Current.Text);
        foreach (var h in sp.GetServices<IHostedService>()) await h.StartAsync(default);   // the watcher's own load is the same one
        Assert.Equal("saved", area.Current.Text);
    }

    [Fact]
    public async Task Saved_versions_replace_the_codes_values_and_can_be_reset_and_restored()
    {
        var (sp, area, store) = await Start(await pg.NewDatabaseAsync());
        await using var _ = sp;
        Assert.Equal(("hello", SettingsSource.Code), (area.Current.Text, area.Source));
        var seen = new List<string>();
        area.Changed += g => seen.Add(g.Text);

        var v1 = await store.SaveAsync("demo.greeting", Doc("hi there", 2), "admin@x", "Friendlier");
        Assert.Equal(1, v1.Number);
        Assert.Equal(("hi there", 2, SettingsSource.Saved), (area.Current.Text, area.Current.Times, area.Source));
        await store.SaveAsync("demo.greeting", Doc("hey", 3), "admin@x", "Shorter");

        var reset = await store.ResetAsync("demo.greeting", "admin@x", "Back to the app's");
        Assert.Null(reset.Document);
        Assert.Equal(("hello", SettingsSource.Code), (area.Current.Text, area.Source));

        var restored = await store.RestoreAsync(v1.Id, "dev@x", "That one was better");
        Assert.Equal((4, v1.Id), (restored.Number, restored.RestoredFrom));
        Assert.Equal("hi there", area.Current.Text);
        Assert.Equal(["hi there", "hey", "hello", "hi there"], seen);

        var history = await store.HistoryAsync("demo.greeting");
        Assert.Equal([4, 3, 2, 1], history.Select(h => h.Number));
        Assert.Equal(("dev@x", "That one was better"), (history[0].By, history[0].Reason));
    }

    [Fact]
    public async Task Bad_settings_and_unknown_areas_are_refused()
    {
        var (sp, area, store) = await Start(await pg.NewDatabaseAsync());
        await using var _ = sp;
        var bad = await Assert.ThrowsAsync<SettingsValidationException>(() => store.SaveAsync("demo.greeting", Doc("x", 0), null, null));
        Assert.Equal(["Times must be at least 1."], bad.Errors);
        var shape = await Assert.ThrowsAsync<SettingsValidationException>(() => store.SaveAsync("demo.greeting", new JsonObject { ["times"] = "lots" }, null, null));
        Assert.Contains("expected shape", shape.Errors[0]);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.SaveAsync("demo.nope", Doc("x", 1), null, null));
        Assert.Empty(await store.HistoryAsync());
        Assert.Equal("hello", area.Current.Text);
    }

    [Fact]
    public async Task Other_instances_pick_changes_up()
    {
        var db = await pg.NewDatabaseAsync();
        var (a, _, storeA) = await Start(db);
        var (b, areaB, _) = await Start(db, poll: TimeSpan.FromMilliseconds(100));
        await using (a)
        await using (b)
        {
            await storeA.SaveAsync("demo.greeting", Doc("from A", 5), "admin@x", null);
            for (var i = 0; i < 50 && areaB.Current.Text != "from A"; i++) await Task.Delay(100);
            Assert.Equal(("from A", 5), (areaB.Current.Text, areaB.Current.Times));
        }
        // A new instance starts with the saved version.
        var (c, areaC, _) = await Start(db);
        await using (c) Assert.Equal("from A", areaC.Current.Text);
    }

    [Fact]
    public async Task A_saved_version_the_app_can_no_longer_read_leaves_the_last_good_one()
    {
        var db = await pg.NewDatabaseAsync();
        var (sp, area, store) = await Start(db);
        await using (sp) await store.SaveAsync("demo.greeting", Doc("good", 2), null, null);
        await using (var conn = new NpgsqlConnection(db))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("""INSERT INTO winche_settings (area, number, document, at) VALUES ('demo.greeting', 2, '{"text": 5, "times": "x"}', now())""", conn);
            await cmd.ExecuteNonQueryAsync();
        }
        var (again, areaAgain, _) = await Start(db);
        await using (again) Assert.Equal("hello", areaAgain.Current.Text);   // the code's values: the latest can't be read
    }

    [Fact]
    public async Task Secrets_are_encrypted_bound_to_their_name_and_never_need_reading_back()
    {
        var db = await pg.NewDatabaseAsync();
        const string key = "a-master-key-that-is-long-enough-1234";
        var (sp, _, _) = await Start(db, key);
        await using (sp)
        {
            var secrets = sp.GetRequiredService<SecretStore>();
            await secrets.SetAsync("keycloak.clientSecret", "s3cr3t-value", "admin@x");
            await secrets.SetAsync("smtp.password", "another", "admin@x");
            Assert.Equal("s3cr3t-value", await secrets.GetAsync("keycloak.clientSecret"));
            Assert.Equal("admin@x", (await secrets.InfoAsync("keycloak.clientSecret"))?.SetBy);
            Assert.Null(await secrets.GetAsync("nope"));

            await using var conn = new NpgsqlConnection(db);
            await conn.OpenAsync();
            await using (var raw = new NpgsqlCommand("SELECT ciphertext FROM winche_secrets WHERE name = 'keycloak.clientSecret'", conn))
                Assert.DoesNotContain("s3cr3t", System.Text.Encoding.UTF8.GetString((byte[])(await raw.ExecuteScalarAsync())!));
            // Copying one secret's ciphertext onto another name doesn't make it readable there.
            await using (var swap = new NpgsqlCommand("UPDATE winche_secrets SET ciphertext = (SELECT ciphertext FROM winche_secrets WHERE name = 'keycloak.clientSecret') WHERE name = 'smtp.password'", conn))
                await swap.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => secrets.GetAsync("smtp.password"));
            Assert.True(await secrets.DeleteAsync("smtp.password"));
        }

        // Another key can't read them; no key can't save them.
        var (wrong, _, _) = await Start(db, "a-different-master-key-also-long-enough");
        await using (wrong) await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.GetRequiredService<SecretStore>().GetAsync("keycloak.clientSecret"));
        var (none, _, _) = await Start(db);
        await using (none)
        {
            Assert.False(none.GetRequiredService<SecretStore>().Available);
            await Assert.ThrowsAsync<InvalidOperationException>(() => none.GetRequiredService<SecretStore>().SetAsync("x", "y", null));
        }
    }

    [Fact]
    public void Without_the_store_areas_use_the_codes_values()
    {
        var services = new ServiceCollection();
        var area = services.AddWincheSettingsArea("demo.greeting", "Greeting", new Greeting("hello", 1));
        Assert.Equal(("hello", SettingsSource.Code), (area.Current.Text, area.Source));
        Assert.Throws<InvalidOperationException>(() => services.AddWincheSettingsArea("demo.greeting", "Again", new Greeting("x", 1)));
    }
}
