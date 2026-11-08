using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services;

public enum ManifestTrust
{
    /// <summary>A file the user opened themselves. Risky extras are reported but kept.</summary>
    LocalFile,

    /// <summary>Received from another device. Anything that could run code beyond a plain package install is removed.</summary>
    Network
}

/// <summary>
/// Validates manifest records before they reach the installer. A manifest decides which
/// arguments are passed to winget.exe, so a crafted one could otherwise smuggle in
/// <c>--override</c>, <c>--manifest</c>, or a different <c>--source</c>.
/// </summary>
public static class ManifestSanitizer
{
    public const int MaxNetworkApps = 5000;
    private const int MaxNameLength = 200;
    private const int MaxTextLength = 1000;

    private static readonly Regex ControlChars = new(@"[\x00-\x1f\x7f]", RegexOptions.Compiled);

    // WinGet flags a manifest may carry. Flags the GUI sets itself (--scope, --version, --force,
    // --silent) are excluded too, because passing them twice makes winget fail.
    private static readonly HashSet<string> AllowedValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--architecture", "-a", "--locale", "--installer-type"
    };

    private static readonly HashSet<string> AllowedSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "--skip-dependencies", "--uninstall-previous", "--no-upgrade"
    };

    public static List<string> Sanitize(ManifestDocument document, ManifestTrust trust)
    {
        var warnings = new List<string>();
        document.Apps ??= new List<AppRecord>();
        document.Apps.RemoveAll(record => record == null);

        if (trust == ManifestTrust.Network && document.Apps.Count > MaxNetworkApps)
        {
            warnings.Add($"Received {document.Apps.Count} apps; only the first {MaxNetworkApps} were kept.");
            document.Apps.RemoveRange(MaxNetworkApps, document.Apps.Count - MaxNetworkApps);
        }

        foreach (AppRecord record in document.Apps)
        {
            SanitizeRecord(record, trust, warnings);
        }
        return warnings;
    }

    private static void SanitizeRecord(AppRecord record, ManifestTrust trust, List<string> warnings)
    {
        record.Name = Clean(record.Name, MaxNameLength);
        record.Id = Clean(record.Id, MaxTextLength).Trim();
        record.Notes = Clean(record.Notes, MaxTextLength);
        record.Publisher = Clean(record.Publisher, MaxNameLength);
        record.Version = Clean(record.Version, MaxNameLength);
        record.Category = Clean(record.Category, MaxNameLength);
        record.StoreSearchName = Clean(record.StoreSearchName, MaxNameLength);
        string label = record.Name.Length > 0 ? record.Name : "(unnamed)";

        if (trust == ManifestTrust.Network)
        {
            // The GUI never runs download installers, and a peer has no business sending them.
            record.DownloadUrl = "";
            record.Sha256 = "";
            record.InstallerType = "";
            record.InstallerArgs = "";
            if (string.Equals(record.InstallType, "download", StringComparison.OrdinalIgnoreCase)) record.InstallType = "manual";
        }

        string type = AppClassifier.NormalizeInstallType(record.ToItem());
        bool validId = type switch
        {
            "winget" => PackageIds.IsWingetId(record.Id),
            "msstore" => PackageIds.IsStoreId(record.Id),
            _ => true
        };
        if (!validId)
        {
            warnings.Add($"{label}: \"{TextUtil.Shorten(record.Id, 60)}\" is not a valid {type} ID; marked as a manual install.");
            record.InstallType = "manual";
            // Also clear the source, or classification would promote it back to a package install.
            record.Source = "manifest";
            record.Notes = "The package ID in the manifest was not valid. Reinstall manually.";
            record.MatchConfidence = MatchConfidence.None;
        }

        if (string.IsNullOrWhiteSpace(record.WingetArgs)) return;
        List<string> kept = FilterWingetArgs(CommandLine.Split(record.WingetArgs), out List<string> rejected);
        if (rejected.Count == 0) return;

        if (trust == ManifestTrust.Network)
        {
            record.WingetArgs = CommandLine.Join(kept);
            warnings.Add($"{label}: removed WinGet arguments {string.Join(" ", rejected)}");
        }
        else
        {
            warnings.Add($"{label}: manifest passes extra WinGet arguments {string.Join(" ", rejected)}. Check that you trust this file.");
        }
    }

    internal static List<string> FilterWingetArgs(IReadOnlyList<string> tokens, out List<string> rejected)
    {
        var kept = new List<string>();
        rejected = new List<string>();
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            string flag = token.Split('=', 2)[0];
            bool inlineValue = token.Contains('=');
            bool nextIsValue = i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-');

            if (AllowedValueFlags.Contains(flag))
            {
                if (inlineValue)
                {
                    kept.Add(token);
                }
                else if (nextIsValue)
                {
                    kept.Add(token);
                    kept.Add(tokens[++i]);
                }
                else
                {
                    rejected.Add(token);
                }
            }
            else if (AllowedSwitches.Contains(token))
            {
                kept.Add(token);
            }
            else
            {
                rejected.Add(token);
                // Treat a following non-flag token as this flag's value so it cannot stand alone.
                if (token.StartsWith('-') && !inlineValue && nextIsValue) rejected.Add(tokens[++i]);
            }
        }
        return kept;
    }

    private static string Clean(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        string cleaned = ControlChars.Replace(value, " ");
        return cleaned.Length <= max ? cleaned : cleaned[..max];
    }
}
