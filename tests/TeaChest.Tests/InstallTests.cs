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

public class InstallClassificationTests
{
    private static (string, string) Classify(int code, string output = "", bool timedOut = false, bool cancelled = false)
        => InstallService.Classify(new ProcessResult(code, output, timedOut), cancelled);

    [Fact]
    public void ZeroIsInstalled() => Assert.Equal("Installed", Classify(0).Item1);

    [Theory]
    [InlineData(unchecked((int)0x8A150109))]
    [InlineData(3010)]
    public void RebootCodesAreNotFailures(int code) => Assert.Equal("RebootRequired", Classify(code).Item1);

    [Theory]
    [InlineData(unchecked((int)0x8A15002B))]
    [InlineData(unchecked((int)0x8A150061))]
    [InlineData(unchecked((int)0x8A15010E))]
    public void AlreadyInstalledCodes(int code) => Assert.Equal("AlreadyInstalled", Classify(code).Item1);

    [Fact]
    public void HashMismatchByCodeNotText()
    {
        // Localized output with no English keywords.
        var (status, detail) = Classify(unchecked((int)0x8A150011), "Der Hash des Installationsprogramms stimmt nicht überein");
        Assert.Equal("Failed", status);
        Assert.Contains("hash", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelWins() => Assert.Equal("Cancelled", Classify(-1, cancelled: true).Item1);

    [Fact]
    public void TimeoutFails() => Assert.Equal("Failed", Classify(-1, timedOut: true).Item1);

    [Fact]
    public void UnknownCodeUsesLastLine()
        => Assert.Equal("Something specific broke", Classify(1, "line one\r\nSomething specific broke\r\n").Item2);
}
