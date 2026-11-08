using System.Text;
using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>
/// Writes a WinGet Configuration (DSC) file next to the manifest, so a new PC can be
/// rebuilt with <c>winget configure</c> without TeaChest being installed at all.
/// </summary>
internal static class WingetConfigurationWriter
{
    private const string Header =
        "# yaml-language-server: $schema=https://aka.ms/configuration-dsc-schema/0.2\n" +
        "# Made by TeaChest. Run it with: winget configure -f <this file>\n" +
        "properties:\n" +
        "  configurationVersion: 0.2.0\n" +
        "  resources:\n";

    /// <summary>Returns the file text, or null when no app in the list can be installed by winget configure.</summary>
    internal static string? Build(IEnumerable<AppRecord> apps)
    {
        var resources = new StringBuilder();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AppRecord app in apps)
        {
            if (!app.Include) continue;

            string installType = (app.InstallType ?? "").Trim();
            bool fromWinget = installType.Equals("winget", StringComparison.OrdinalIgnoreCase)
                              && PackageIds.IsWingetId(app.Id);
            bool fromStore = installType.Equals("msstore", StringComparison.OrdinalIgnoreCase)
                             && PackageIds.IsStoreId(app.Id);
            if (!fromWinget && !fromStore) continue;

            // The schema caps resource ids at 128 characters.
            if (app.Id.Length > 128) continue;

            // Two rows for the same package would make winget configure do the work twice.
            if (!seen.Add(app.Id)) continue;

            string source = fromStore ? "msstore" : "winget";
            string id = Quote(app.Id);
            resources.Append("    - resource: Microsoft.WinGet.DSC/WinGetPackage\n");
            resources.Append($"      id: {id}\n");
            resources.Append("      directives:\n");
            resources.Append($"        description: {Quote(Description(app.Name))}\n");
            resources.Append("      settings:\n");
            resources.Append($"        id: {id}\n");
            resources.Append($"        source: {source}\n");
        }

        return resources.Length == 0 ? null : Header + resources.ToString();
    }

    // YAML single quotes escape by doubling, and a control character would break the line.
    private static string Quote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string Description(string? name)
    {
        string flattened = new((name ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray());

        // The schema caps directives.description at 512 characters, and a registry name can be any length.
        // Shorten first, so the quote-doubling below can never leave half of an escaped pair behind.
        return TextUtil.Shorten(flattened, 512);
    }
}
