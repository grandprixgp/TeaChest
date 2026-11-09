using System.Text.RegularExpressions;

namespace SettingsProbe;

internal enum Verdict
{
    /// <summary>Looks like settings or user data worth offering.</summary>
    Offer,
    /// <summary>A cache, log, temp or crash file. Never offered.</summary>
    Exclude,
    /// <summary>Could hold a secret or machine-bound data. Never offered without a recipe saying so.</summary>
    NeverCopy,
    /// <summary>Created and gone again during the session, so nothing to carry.</summary>
    Transient
}

internal sealed record LearnedItem(string RelativePath, Verdict Verdict, string Reason);

/// <summary>
/// The core idea in miniature: take what an app was seen writing, drop the noise, and suggest
/// recipe entries. The patterns are the spec's "what not to carry" rules, so the prototype and
/// the spec stay in step.
/// </summary>
internal static class LearnedRecipe
{
    private static readonly (Regex Pattern, string Reason)[] ExcludeRules =
    {
        (Rx(@"(^|\\)(Cache|Code Cache|GPUCache|DawnCache|ShaderCache|LocalCache|INetCache|blob_storage|Crashpad|CrashDumps|Temp|TempState|logs?)(\\|$)"), "cache, log or temp folder"),
        (Rx(@"\.(log|tmp|temp|dmp|bak|old|lock|etl)$"), "log, temp or dump file"),
        (Rx(@"(^|\\)~\$"), "Office-style lock file"),
    };

    private static readonly (Regex Pattern, string Reason)[] NeverCopyRules =
    {
        (Rx(@"(^|\\)(Cookies|Login Data|Web Data|Local State|Local Storage|Session Storage|IndexedDB|Network)(\\|$)"), "browser or Electron credential store"),
        (Rx(@"(^|\\)(\.git-credentials|\.npmrc|\.pypirc|\.netrc|credentials(\.json)?|hosts\.yml|auth\.json|token[^\\]*)$"), "credentials file"),
        (Rx(@"\.(pem|key|pfx|p12|kdbx|lic|license)$"), "key, vault or licence file"),
        (Rx(@"(^|\\)id_(rsa|dsa|ecdsa|ed25519)$"), "SSH private key"),
        (Rx(@"(^|\\)settings\.dat$"), "machine-bound app hive"),
    };

    internal static Verdict Classify(string relativePath, out string reason)
    {
        foreach (var (pattern, why) in NeverCopyRules)
        {
            if (pattern.IsMatch(relativePath)) { reason = why; return Verdict.NeverCopy; }
        }
        foreach (var (pattern, why) in ExcludeRules)
        {
            if (pattern.IsMatch(relativePath)) { reason = why; return Verdict.Exclude; }
        }
        reason = "written by the app";
        return Verdict.Offer;
    }

    /// <summary>Files only (folders are implied by their contents), in a stable order.</summary>
    internal static IReadOnlyList<LearnedItem> Build(IEnumerable<(string RelativePath, bool IsDirectory, bool Deleted, bool MovedOut)> seen)
    {
        var items = new List<LearnedItem>();
        foreach (var s in seen)
        {
            if (s.IsDirectory) continue;
            if (s.Deleted || s.MovedOut)
            {
                items.Add(new LearnedItem(s.RelativePath, Verdict.Transient, s.Deleted ? "deleted again" : "moved away"));
                continue;
            }
            Verdict verdict = Classify(s.RelativePath, out string reason);
            items.Add(new LearnedItem(s.RelativePath, verdict, reason));
        }
        return items
            .GroupBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(i => i.Verdict)
            .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
