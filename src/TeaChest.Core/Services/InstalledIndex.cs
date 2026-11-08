using TeaChest.Models;

namespace TeaChest.Services;

public sealed class InstalledIndex
{
    // Names keep their version numbers (NormalizeStrict) so "Python 3.11" does not satisfy "Python 3.12".
    public HashSet<string> Names { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Ids { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> PackageFamilies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when WinGet's own inventory loaded, so package IDs can be trusted on their own.</summary>
    public bool HasWingetInventory { get; set; }

    // Filling one from this PC's own inventory is per-OS: see WindowsInstalledIndex in the app.

    public bool Contains(AppItem app)
    {
        if (!string.IsNullOrWhiteSpace(app.Id) && Ids.Contains(app.Id)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFamilyName) && PackageFamilies.Contains(app.PackageFamilyName)) return true;
        if (!string.IsNullOrWhiteSpace(app.PackageFullName) && Ids.Contains(app.PackageFullName)) return true;

        // WinGet correlates installed programs to package IDs, so when its inventory is available a
        // missing ID means "not installed". Guessing by name here causes false skips.
        bool hasPackageId = !string.IsNullOrWhiteSpace(app.Id) &&
                            (app.IsWinget || string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase));
        if (hasPackageId && HasWingetInventory) return false;

        string name = TextUtil.NormalizeStrict(app.Name);
        return name.Length > 0 && Names.Contains(name);
    }

    internal void AddName(string name)
    {
        string key = TextUtil.NormalizeStrict(name);
        if (key.Length > 0) Names.Add(key);
    }
}
