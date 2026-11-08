using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public class InstalledIndexTests
{
    private static WingetInventory Inventory(params (string Name, string Id)[] rows)
        => new(rows.Select(r => new WingetRow(r.Name, r.Id, "1.0", "winget", false, false, false, false)).ToList(), null);

    private static AppItem Winget(string name, string id)
        => new() { Name = name, Id = id, Source = "winget", InstallType = "winget" };

    [Fact]
    public void OlderPythonDoesNotCountAsNewerPython()
    {
        var index = WindowsInstalledIndex.Build(
            new[] { new AppItem { Name = "Python 3.11.9 (64-bit)" } },
            Array.Empty<AppItem>(),
            Inventory(("Python 3.11.9 (64-bit)", "Python.Python.3.11")));

        Assert.False(index.Contains(Winget("Python 3.12.7 (64-bit)", "Python.Python.3.12")));
        Assert.True(index.Contains(Winget("Python 3.11.9 (64-bit)", "Python.Python.3.11")));
    }

    [Fact]
    public void WithWingetInventoryIdsDecide()
    {
        var index = WindowsInstalledIndex.Build(
            new[] { new AppItem { Name = "Google Chrome" } },
            Array.Empty<AppItem>(),
            Inventory(("Git", "Git.Git")));

        // Same display name, but WinGet did not correlate it to this ID.
        Assert.False(index.Contains(Winget("Google Chrome", "Google.Chrome")));
    }

    [Fact]
    public void WithoutWingetFallsBackToVersionSensitiveNames()
    {
        var index = WindowsInstalledIndex.Build(
            new[] { new AppItem { Name = "Google Chrome" }, new AppItem { Name = "Python 3.11.9" } },
            Array.Empty<AppItem>(),
            new WingetInventory(Array.Empty<WingetRow>(), "winget not found"));

        Assert.True(index.Contains(Winget("Google Chrome", "Google.Chrome")));
        Assert.False(index.Contains(Winget("Python 3.12.7", "Python.Python.3.12")));
    }

    [Fact]
    public void ExportedIdsCount()
    {
        var inventory = new WingetInventory(
            new[] { new WingetRow("Git", "Git.Git", "2", "winget", false, false, false, false) },
            null,
            new[] { new ExportedPackage("Microsoft.VisualStudioCode.Insiders", "1.96", "winget") });
        var index = WindowsInstalledIndex.Build(Array.Empty<AppItem>(), Array.Empty<AppItem>(), inventory);

        Assert.True(index.Contains(Winget("VS Code Insiders", "Microsoft.VisualStudioCode.Insiders")));
    }
}
