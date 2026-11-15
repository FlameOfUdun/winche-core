using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Winche.Core.Tests;

public class WincheErrorTests
{
    [Fact]
    public void Every_code_has_a_kebab_name_that_reads_back()
    {
        foreach (var code in Enum.GetValues<WincheErrorCode>())
        {
            var name = WincheError.Name(code);
            Assert.Matches("^[a-z]+(-[a-z]+)*$", name);
            Assert.Equal(code, WincheError.Parse(name));
        }
        Assert.Equal("permission-denied", WincheError.Name(WincheErrorCode.PermissionDenied));
        Assert.Equal(WincheErrorCode.Unknown, WincheError.Parse("NOT_A_CODE"));
    }

    [Fact]
    public void Http_statuses_follow_one_table()
    {
        Assert.Equal(400, WincheError.StatusOf(WincheErrorCode.InvalidArgument));
        Assert.Equal(401, WincheError.StatusOf(WincheErrorCode.Unauthenticated));
        Assert.Equal(403, WincheError.StatusOf(WincheErrorCode.PermissionDenied));
        Assert.Equal(404, WincheError.StatusOf(WincheErrorCode.NotFound));
        Assert.Equal(409, WincheError.StatusOf(WincheErrorCode.AlreadyExists));
        Assert.Equal(429, WincheError.StatusOf(WincheErrorCode.ResourceExhausted));
        Assert.Equal(503, WincheError.StatusOf(WincheErrorCode.Unavailable));
        Assert.Equal(500, WincheError.StatusOf(WincheErrorCode.Internal));
    }

    [Fact]
    public void The_body_is_error_with_code_and_message_and_only_the_extras_given()
    {
        var plain = JsonSerializer.Serialize(new WincheError(WincheErrorCode.NotFound, "No such file.").ToBody());
        Assert.Equal("""{"error":{"code":"not-found","message":"No such file."}}""", plain);
        var full = JsonNode.Parse(JsonSerializer.Serialize(
            new WincheError(WincheErrorCode.Unauthenticated, "Enter your code.", "MFA_REQUIRED", new JsonObject { ["mfaToken"] = "m" }).ToBody()))!;
        Assert.Equal("MFA_REQUIRED", (string?)full["error"]!["reason"]);
        Assert.Equal("m", (string?)full["error"]!["details"]!["mfaToken"]);
    }
}
