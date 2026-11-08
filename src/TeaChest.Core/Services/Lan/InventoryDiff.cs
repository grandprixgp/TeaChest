using TeaChest.Models;

namespace TeaChest.Services.Lan;

public sealed class InventoryDiffResult
{
    public List<AppItem> MissingHere { get; } = new();
    public List<AppItem> AlreadyHere { get; } = new();
    public List<AppItem> OnlyHere { get; } = new();
}

// TODO: "time machine" scheduling - quietly saving a dated manifest to OneDrive every week or so
// is still undecided: whether it's on by default, and whether it runs in the background or only
// when the app opens. Most migrations happen *after* a PC dies, and by then it's too late to scan
// it, so the saving matters more than the diff.
public static class InventoryDiff
{
    /// <summary>
    /// Splits a remote inventory into apps this PC lacks and apps it already has, and lists local
    /// apps the remote does not have. Missing apps keep the remote's selection; apps already here
    /// are deselected so they never end up in an install plan.
    /// </summary>
    public static InventoryDiffResult Compare(IEnumerable<AppItem> remote, InstalledIndex installedHere, IEnumerable<AppItem>? localApps)
    {
        var result = new InventoryDiffResult();
        List<AppItem> remoteApps = remote.ToList();
        foreach (AppItem app in remoteApps)
        {
            if (installedHere.Contains(app))
            {
                app.IsSelected = false;
                result.AlreadyHere.Add(app);
            }
            else
            {
                result.MissingHere.Add(app);
            }
        }

        if (localApps != null)
        {
            var remoteIds = new HashSet<string>(remoteApps.Where(a => a.Id.Length > 0).Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
            var remoteNames = new HashSet<string>(remoteApps.Select(a => TextUtil.NormalizeStrict(a.Name)), StringComparer.OrdinalIgnoreCase);
            foreach (AppItem local in localApps)
            {
                bool onRemote = (local.Id.Length > 0 && remoteIds.Contains(local.Id)) || remoteNames.Contains(TextUtil.NormalizeStrict(local.Name));
                if (!onRemote) result.OnlyHere.Add(local);
            }
        }
        return result;
    }

    /// <summary>
    /// Compares two saved manifests: apps the before one had and the after one lacks, apps both
    /// have, and apps only the after one has. Both documents must already have been through
    /// <see cref="ManifestSanitizer"/>; this trusts their IDs and does not sanitise again.
    /// Apps match by ID when both sides have one, and by strict name when either side does not,
    /// because a WinGet outage leaves an app with no ID on one side only.
    /// </summary>
    public static InventoryDiffResult CompareManifests(ManifestDocument before, ManifestDocument after)
    {
        var result = new InventoryDiffResult();
        InstalledIndex afterIndex = FromManifest(after);
        InstalledIndex beforeIndex = FromManifest(before);
        HashSet<string> unidentifiedAfter = UnidentifiedNames(after);
        HashSet<string> unidentifiedBefore = UnidentifiedNames(before);

        foreach (AppRecord record in before.Apps)
        {
            AppItem app = record.ToItem();
            if (IsPresent(app, afterIndex, unidentifiedAfter)) result.AlreadyHere.Add(app);
            else result.MissingHere.Add(app);
        }

        foreach (AppRecord record in after.Apps)
        {
            AppItem app = record.ToItem();
            if (!IsPresent(app, beforeIndex, unidentifiedBefore)) result.OnlyHere.Add(app);
        }

        return result;
    }

    private static bool IsPresent(AppItem app, InstalledIndex index, HashSet<string> unidentifiedNames)
    {
        if (index.Contains(app)) return true;
        string name = TextUtil.NormalizeStrict(app.Name);
        return name.Length > 0 && unidentifiedNames.Contains(name);
    }

    /// <summary>Strict names of rows with no ID, so a one-sided WinGet outage still matches by name.</summary>
    private static HashSet<string> UnidentifiedNames(ManifestDocument document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppRecord app in document.Apps)
        {
            if (!string.IsNullOrWhiteSpace(app.Id)) continue;
            string name = TextUtil.NormalizeStrict(app.Name);
            if (name.Length > 0) names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Indexes a manifest as if it were this PC's inventory. The manifest was scanned on a real PC,
    /// so its IDs can be trusted on their own; rows with no ID are matched by
    /// <see cref="CompareManifests"/> against the other side's names.
    /// </summary>
    internal static InstalledIndex FromManifest(ManifestDocument document)
    {
        var index = new InstalledIndex { HasWingetInventory = true };
        foreach (AppRecord app in document.Apps)
        {
            string name = TextUtil.NormalizeStrict(app.Name);
            if (name.Length > 0) index.Names.Add(name);
            if (!string.IsNullOrWhiteSpace(app.Id)) index.Ids.Add(app.Id);
            if (!string.IsNullOrWhiteSpace(app.PackageFamilyName)) index.PackageFamilies.Add(app.PackageFamilyName);
            if (!string.IsNullOrWhiteSpace(app.PackageFullName)) index.Ids.Add(app.PackageFullName);
        }

        return index;
    }
}
