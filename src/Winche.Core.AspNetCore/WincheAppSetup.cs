using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Winche;

/// <summary>One Winche service an app can use, and where it is under the server's address.</summary>
public sealed record WincheAppSetupService(string Id, string Path, string? LivePath = null);

/// <summary>The registered app a setup is for (<c>?app=</c>).</summary>
public sealed record WincheAppSetupApp(string Id, string Name);

/// <summary>A sign-in provider an app can offer, with its public client ids.</summary>
public sealed record WincheAppSetupProvider(string Name, string Kind, string DisplayName, IReadOnlyList<string> ClientIds);

/// <summary>
/// What an app needs to know to use this server: published at <c>GET /.well-known/winche</c> (nothing in it is
/// secret), and turned into <c>lib/winche_options.dart</c> by <c>winche configure</c> and the console's Project settings → General.
/// </summary>
public sealed record WincheAppSetup(string ProjectId, string? Name, string BaseUrl, IReadOnlyList<WincheAppSetupService> Services,
    IReadOnlyList<WincheAppSetupProvider> Providers, WincheAppSetupApp? App = null)
{
    /// <summary>The platforms <see cref="DartOptionsFile"/> writes when not told which.</summary>
    public static readonly IReadOnlyList<string> AllPlatforms = ["android", "ios", "macos", "web", "windows", "linux"];

    /// <summary>The address the request came in on, behind a proxy too (with forwarded headers applied).</summary>
    public static string BaseUrlOf(HttpRequest request) => $"{request.Scheme}://{request.Host}{request.PathBase}";

    /// <summary>
    /// <c>lib/winche_options.dart</c>: a <c>DefaultWincheOptions</c> with one set of options per platform in
    /// <paramref name="platforms"/> (all of them by default) and <c>currentPlatform</c> choosing between them.
    /// A provider with one client id gets it on every platform; one with several gets a note listing them, since
    /// which belongs to which platform is the app's to say.
    /// </summary>
    public string DartOptionsFile(IEnumerable<string>? platforms = null)
    {
        var chosen = (platforms ?? AllPlatforms).Select(p => p.Trim().ToLowerInvariant()).Where(AllPlatforms.Contains).Distinct().ToList();
        if (chosen.Count == 0) chosen = [.. AllPlatforms];
        var b = new StringBuilder();
        b.Append("// Written by `winche configure` from ").Append(BaseUrl).Append(".\n");
        b.Append("// Run it again after adding a platform or a sign-in provider.\n");
        if (App is null) b.Append("// No app id: register the app (`winche apps:create <name>`, or in the console), then `winche configure --app <its id>`.\n");
        b.Append("//\n// ignore_for_file: type=lint\n");
        b.Append("import 'package:winche_flutter/winche_flutter.dart';\n\n");
        b.Append("/// Winche options for each platform this app runs on, for project `").Append(ProjectId).Append("`.\n");
        b.Append("///\n/// ```dart\n");
        b.Append("/// final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);\n");
        b.Append("/// if (kDebugMode) app.useEmulator();   // `winche emulators start`\n");
        b.Append("/// ```\n");
        b.Append("abstract final class DefaultWincheOptions {\n");
        b.Append("  /// The options for the platform this runs on.\n");
        b.Append("  static WincheOptions get currentPlatform => WincheFlutter.select(\n");
        foreach (var p in chosen) b.Append("        ").Append(p).Append(": ").Append(p).Append(",\n");
        b.Append("      );\n");
        foreach (var p in chosen)
        {
            b.Append("\n  static final ").Append(p).Append(" = WincheOptions(\n");
            b.Append("    projectId: '").Append(Escape(ProjectId)).Append("',\n");
            if (App is not null) b.Append("    appId: '").Append(Escape(App.Id)).Append("',   // ").Append(App.Name.Replace("\n", " ")).Append('\n');
            b.Append("    baseUrl: Uri.parse('").Append(Escape(BaseUrl)).Append("'),\n");
            var single = Providers.Where(x => x.ClientIds.Count == 1).ToList();
            var several = Providers.Where(x => x.ClientIds.Count > 1).ToList();
            foreach (var x in several)
                b.Append("    // ").Append(x.Name).Append(": one of ").Append(string.Join(", ", x.ClientIds))
                 .Append(" — add this platform's to clientIds.\n");
            if (single.Count > 0)
            {
                b.Append("    clientIds: {\n");
                foreach (var x in single) b.Append("      '").Append(Escape(x.Name)).Append("': '").Append(Escape(x.ClientIds[0])).Append("',\n");
                b.Append("    },\n");
            }
            b.Append("  );\n");
        }
        b.Append("}\n");
        return b.ToString();
    }

    /// <summary>What to add to <c>main()</c>, for the console to show next to the file.</summary>
    public static string DartSnippet =>
        "import 'package:flutter/foundation.dart';\n" +
        "import 'package:winche_flutter/winche_flutter.dart';\n\n" +
        "import 'winche_options.dart';\n\n" +
        "Future<void> main() async {\n" +
        "  WidgetsFlutterBinding.ensureInitialized();\n" +
        "  final app = await WincheFlutter.initializeApp(options: DefaultWincheOptions.currentPlatform);\n" +
        "  if (kDebugMode) app.useEmulator();   // only with `winche emulators start` running\n" +
        "  runApp(const MyApp());\n" +
        "}\n";

    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("$", "\\$");
}

/// <summary>Publishing a <see cref="WincheAppSetup"/>.</summary>
public static class WincheAppSetupEndpoints
{
    /// <summary>
    /// Publishes what <paramref name="describe"/> says at <c>GET /.well-known/winche</c>, for <c>winche configure</c>
    /// and anyone setting up an app: the project, which services the server runs and where, and the sign-in
    /// providers' public client ids. Nothing in it is secret.
    /// </summary>
    public static IEndpointConventionBuilder MapWincheAppSetup(this IEndpointRouteBuilder endpoints, Func<HttpContext, WincheAppSetup> describe) =>
        endpoints.MapGet("/.well-known/winche", (HttpContext http) => Results.Json(describe(http))).AllowAnonymous();
}
