using Xunit;

namespace Winche.Core.Tests;

public class WincheAppSetupTests
{
    private static readonly WincheAppSetup Setup = new("clinic", "Clinic", "https://api.example.com/p/clinic",
        [new("database", "/documents/v1", "/documents/v1/ws"), new("auth", "/auth/v1")],
        [new("google", "google", "Google", ["g.apps.googleusercontent.com"]), new("apple", "apple", "Apple", ["a", "b"])],
        new("clinic-ios", "Clinic iOS"));

    [Fact]
    public void The_options_file_has_one_set_of_options_per_platform_with_the_app_and_its_client_ids()
    {
        var dart = Setup.DartOptionsFile(["android", "ios", "nope"]);
        Assert.Contains("static final android = WincheOptions(", dart);
        Assert.Contains("static final ios = WincheOptions(", dart);
        Assert.DoesNotContain("static final web", dart);
        Assert.Contains("projectId: 'clinic',", dart);
        Assert.Contains("appId: 'clinic-ios',   // Clinic iOS", dart);
        Assert.Contains("baseUrl: Uri.parse('https://api.example.com/p/clinic'),", dart);
        Assert.Contains("'google': 'g.apps.googleusercontent.com',", dart);
        Assert.Contains("// apple: one of a, b", dart);
        // Without an app, it says how to get one; with no platforms named, all of them.
        var plain = (Setup with { App = null }).DartOptionsFile();
        Assert.Contains("No app id: register the app in the console", plain);
        Assert.Equal(WincheAppSetup.AllPlatforms.Count, WincheAppSetup.AllPlatforms.Count(p => plain.Contains($"static final {p} = ")));
    }

    [Theory]
    [InlineData("clinic", true)]
    [InlineData("clinic-records-2", true)]
    [InlineData("ab", false)]
    [InlineData("Clinic", false)]
    [InlineData("2clinic", false)]
    [InlineData("clinic-", false)]
    [InlineData("clinic_records", false)]
    public void Ids_are_short_lower_case_and_dashed(string id, bool valid) => Assert.Equal(valid, WincheIds.IsValid(id));
}
