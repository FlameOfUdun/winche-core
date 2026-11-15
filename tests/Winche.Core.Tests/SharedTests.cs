using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Winche.Core;

namespace Winche.Core.Tests;

public class PathPatternTests
{
    [Theory]
    [InlineData("users/{uid}", "users/u1", true)]
    [InlineData("users/{uid}", "users/u1/notes/n1", false)]
    [InlineData("users/{rest=**}", "users/u1/notes/n1", true)]
    [InlineData("users/{rest=**}", "users", false)]                       // trailing: one or more
    [InlineData("{path=**}/posts/{id}", "posts/p1", true)]                // leading: zero or more
    [InlineData("{path=**}/posts/{id}", "a/b/posts/p1", true)]
    [InlineData("patients/{p}/visits/{v}", "patients/a/visits/b", true)]
    [InlineData("patients/{p}/visits/{v}", "patients/a/notes/b", false)]
    public void Matches(string pattern, string path, bool matches) => Assert.Equal(matches, PathPattern.Parse(pattern).Match(path) is not null);

    [Fact]
    public void Captures_names_and_paths()
    {
        var m = PathPattern.Parse("{dir=**}/files/{name}").Match("a/b/files/x.png")!;
        Assert.Equal(("a/b", "x.png"), (m["dir"], m["name"]));
        Assert.Equal(["dir", "name"], PathPattern.Parse("{dir=**}/files/{name}").Names);
        Assert.Equal("users/{uid}/{rest=**}", PathPattern.Parse("/users/{uid}/{rest=**}/").ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("users/*")]
    [InlineData("users/u{id}")]
    [InlineData("users/{1id}")]
    [InlineData("users/{id=x}")]
    [InlineData("{a=**}/{b=**}")]
    [InlineData("users/{id}/notes/{id}")]
    public void Refuses_malformed_patterns_saying_why(string pattern)
    {
        Assert.False(PathPattern.TryParse(pattern, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Throws<ArgumentException>(() => PathPattern.Parse(pattern));
    }

    [Fact]
    public void Joins_and_matches_prefixes()
    {
        var joined = PathPattern.Of([.. PathPattern.Parse("users/{uid}").Segments, .. PathPattern.Parse("notes/{id}").Segments]);
        Assert.Equal("users/{uid}/notes/{id}", joined.ToString());
        Assert.True(PathPattern.Parse("users/{uid}").TryMatchPrefix("users/u1/notes/n1", out var captures));
        Assert.Equal("u1", captures["uid"]);
    }
}

public class SmallPiecesTests
{
    [Fact]
    public void Base64url_round_trips_and_reads_both_alphabets()
    {
        var path = "users/ünïcode?/a+b";
        Assert.Equal(path, WincheBase64Url.Decode(WincheBase64Url.Encode(path)));
        Assert.DoesNotContain('=', WincheBase64Url.Encode("a"));
        Assert.Equal(path, WincheBase64Url.Decode(Convert.ToBase64String(Encoding.UTF8.GetBytes(path))));   // standard, padded
        Assert.False(WincheBase64Url.TryDecode("not*base64!", out _));
        Assert.Throws<FormatException>(() => WincheBase64Url.Decode("a"));
        Assert.Equal(32, WincheBase64Url.Random(24).Length);
    }

    [Fact]
    public void Keys_are_derived_as_Winche_Server_always_did()
    {
        var master = new string('k', 40);
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(master), Encoding.UTF8.GetBytes("winche-server:file-links")));
        Assert.Equal(expected, new WincheKeys(master).Derive("file-links"));
        Assert.NotEqual(expected, new WincheKeys(master).Derive("auth-signing-keys"));
        Assert.Null(new WincheKeys(null).TryDerive("file-links"));
        Assert.Throws<InvalidOperationException>(() => new WincheKeys("short").Derive("x"));
    }

    [Fact]
    public void Like_escaping() => Assert.Equal(@"a\%b\_c\\d", WincheSql.EscapeLike(@"a%b_c\d"));

    [Fact]
    public async Task The_caller_flows_with_async_work_and_is_restored()
    {
        Assert.Null(WincheCaller.Claims);
        using (WincheCaller.Use(new Dictionary<string, object?> { ["sub"] = "s1" }))
        {
            Assert.Equal("s1", WincheCaller.Uid);
            await Task.Run(() => Assert.Equal("s1", WincheCaller.Uid));
            using (WincheCaller.Use(null)) Assert.True(WincheCaller.IsSet && WincheCaller.Claims is null);
            Assert.Equal("s1", WincheCaller.Uid);
        }
        Assert.False(WincheCaller.IsSet);
        Assert.Equal("u", WincheCaller.UidOf(new Dictionary<string, object?> { ["uid"] = "u", ["sub"] = "s" }));
    }

    [Fact]
    public void An_unexpected_error_is_logged_under_an_id_the_caller_gets()
    {
        var logged = new List<string>();
        var error = WincheError.Unexpected(new InvalidOperationException("secret"), new ListLogger(logged), "GET /x", "u1");
        Assert.Equal(WincheErrorCode.Internal, error.Kind);
        var id = (string)error.Details!["errorId"]!;
        Assert.Equal($"Internal error ({id}).", error.Message);
        Assert.Contains(logged, l => l.Contains(id) && l.Contains("GET /x") && l.Contains("u1"));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(error.ToBody()));
    }

    private sealed class ListLogger(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Add(formatter(state, exception));
    }
}

public class AspNetCoreTests
{
    [Fact]
    public async Task UseWincheCaller_sets_the_caller_for_the_request()
    {
        var services = new ServiceCollection().AddLogging()
            .AddWincheCaller(http => http.Request.Headers["X-Uid"] is { Count: > 0 } uid ? new Dictionary<string, object?> { ["uid"] = uid.ToString() } : null)
            .BuildServiceProvider();
        var app = new ApplicationBuilder(services);
        string? seen = "unset";
        app.UseWincheCaller();
        app.Run(async http => { seen = WincheCaller.Uid; await Task.Yield(); seen ??= (await http.WincheCallerOf()) is null ? null : "?"; });
        var pipeline = app.Build();

        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Headers["X-Uid"] = "ann";
        await pipeline(http);
        Assert.Equal("ann", seen);
        Assert.Equal("ann", WincheCaller.UidOf(await http.WincheCallerOf()));

        await pipeline(new DefaultHttpContext { RequestServices = services });
        Assert.Null(seen);
        Assert.False(WincheCaller.IsSet);   // nothing leaks out of the request
    }

    [Fact]
    public async Task The_error_filter_maps_known_failures_and_hides_unexpected_ones()
    {
        var filter = new Filter();
        async Task<IResult> Run(Exception ex)
        {
            var http = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
            http.Request.Method = "GET";
            http.Request.Path = "/x";
            return (IResult)(await filter.InvokeAsync(new DefaultEndpointFilterInvocationContext(http), _ => throw ex))!;
        }
        Assert.Equal(404, ((IStatusCodeHttpResult)await Run(new KeyNotFoundException("no"))).StatusCode);
        Assert.Equal(400, ((IStatusCodeHttpResult)await Run(new BadHttpRequestException("bad body"))).StatusCode);
        Assert.Equal(500, ((IStatusCodeHttpResult)await Run(new InvalidOperationException("boom"))).StatusCode);
    }

    private sealed class Filter() : WincheErrorFilter(NullLogger.Instance)
    {
        protected override WincheError? Map(Exception exception) =>
            exception is KeyNotFoundException ? new WincheError(WincheErrorCode.NotFound, exception.Message) : null;
    }
}

[Collection("postgres")]
public class SchemaTests(PostgresFixture pg)
{
    [Fact]
    public async Task Steps_apply_once_in_order_and_new_ones_later()
    {
        await using var source = NpgsqlDataSource.Create(await pg.NewDatabaseAsync());
        string[] v1 = ["CREATE TABLE IF NOT EXISTS t (id int)"];
        string[] v2 = [.. v1, "ALTER TABLE t ADD COLUMN IF NOT EXISTS name text", "INSERT INTO t VALUES (1, 'a')"];

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => WincheSchema.EnsureAsync(source, "demo", v1)));   // racing servers
        Assert.All(results, r => Assert.Equal(1, r));
        Assert.Equal(3, await WincheSchema.EnsureAsync(source, "demo", v2));
        Assert.Equal(3, await WincheSchema.EnsureAsync(source, "demo", v2));   // nothing left to do
        await using var count = source.CreateCommand("SELECT count(*) FROM t");
        Assert.Equal(1L, await count.ExecuteScalarAsync());   // the insert ran once
        Assert.Equal(1, await WincheSchema.EnsureAsync(source, "other", v1));   // components are separate
    }
}
