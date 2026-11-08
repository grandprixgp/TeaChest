using System.IO;
using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public class CommandLineTests
{
    [Theory]
    [InlineData("--locale en-US", new[] { "--locale", "en-US" })]
    [InlineData("--override \"/S /D=C:\\Program Files\\App\"", new[] { "--override", "/S /D=C:\\Program Files\\App" })]
    [InlineData("  a   b  ", new[] { "a", "b" })]
    [InlineData("say \\\"hi\\\"", new[] { "say", "\"hi\"" })]
    [InlineData("\"\"", new[] { "" })]
    public void SplitsLikeWindows(string input, string[] expected)
        => Assert.Equal(expected, CommandLine.Split(input));

    [Theory]
    [InlineData("plain")]
    [InlineData("has space")]
    [InlineData("quote\"inside")]
    [InlineData("C:\\trailing\\")]
    [InlineData("")]
    public void JoinRoundTrips(string arg)
        => Assert.Equal(new[] { "x", arg }, CommandLine.Split(CommandLine.Join(new[] { "x", arg })));
}

public class PackageIdTests
{
    [Theory]
    [InlineData("Git.Git", true)]
    [InlineData("Henry++.simplewall", true)]
    [InlineData("Python.Python.3.12", true)]
    [InlineData("NoDots", false)]
    [InlineData("--override.x", false)]
    [InlineData("Evil.Id --source x", false)]
    [InlineData("..\\path.x", false)]
    public void WingetIds(string id, bool valid) => Assert.Equal(valid, PackageIds.IsWingetId(id));

    [Theory]
    [InlineData("9NP83LWLPZ9K", true)]
    [InlineData("9np83lwlpz9k", true)]
    [InlineData("9NP83LWLPZ9", false)]
    [InlineData("Git.Git", false)]
    public void StoreIds(string id, bool valid) => Assert.Equal(valid, PackageIds.IsStoreId(id));
}

public class ManifestSanitizerTests
{
    private static ManifestDocument Doc(params AppRecord[] apps) => new() { Apps = apps.ToList() };

    [Fact]
    public void InvalidWingetIdBecomesManualAndStaysManual()
    {
        var doc = Doc(new AppRecord { Name = "Evil", Id = "--manifest C:\\x.yaml", Source = "winget", InstallType = "winget" });

        var warnings = ManifestSanitizer.Sanitize(doc, ManifestTrust.LocalFile);

        Assert.Single(warnings);
        AppItem item = doc.Apps[0].ToItem();
        AppClassifier.ApplyClassification(item);
        Assert.True(item.IsManual);
    }

    [Fact]
    public void NetworkStripsDangerousArgsButKeepsSafeOnes()
    {
        var doc = Doc(new AppRecord
        {
            Name = "App",
            Id = "Vendor.App",
            InstallType = "winget",
            WingetArgs = "--locale en-US --override \"/S /evil\" --source other --skip-dependencies"
        });

        var warnings = ManifestSanitizer.Sanitize(doc, ManifestTrust.Network);

        Assert.Equal(new[] { "--locale", "en-US", "--skip-dependencies" }, CommandLine.Split(doc.Apps[0].WingetArgs));
        Assert.Contains(warnings, w => w.Contains("--override"));
    }

    [Fact]
    public void LocalFileKeepsArgsButWarns()
    {
        var doc = Doc(new AppRecord { Name = "App", Id = "Vendor.App", InstallType = "winget", WingetArgs = "--override /S" });

        var warnings = ManifestSanitizer.Sanitize(doc, ManifestTrust.LocalFile);

        Assert.Equal("--override /S", doc.Apps[0].WingetArgs);
        Assert.Single(warnings);
    }

    [Fact]
    public void NetworkDropsDownloadInstallers()
    {
        var doc = Doc(new AppRecord
        {
            Name = "Tool",
            InstallType = "download",
            DownloadUrl = "http://example.invalid/tool.exe",
            InstallerArgs = "/quiet"
        });

        ManifestSanitizer.Sanitize(doc, ManifestTrust.Network);

        Assert.Equal("manual", doc.Apps[0].InstallType);
        Assert.Equal("", doc.Apps[0].DownloadUrl);
        Assert.Equal("", doc.Apps[0].InstallerArgs);
    }

    [Fact]
    public void ControlCharactersAndHugeNamesAreCleaned()
    {
        var doc = Doc(new AppRecord { Name = "Bad\u0007Name" + new string('x', 500), Id = "Vendor.App", InstallType = "winget" });

        ManifestSanitizer.Sanitize(doc, ManifestTrust.Network);

        Assert.DoesNotContain('\u0007', doc.Apps[0].Name);
        Assert.True(doc.Apps[0].Name.Length <= 200);
    }

    [Fact]
    public void NetworkCapsAppCount()
    {
        var doc = Doc(Enumerable.Range(0, ManifestSanitizer.MaxNetworkApps + 10)
            .Select(i => new AppRecord { Name = "App " + i }).ToArray());

        ManifestSanitizer.Sanitize(doc, ManifestTrust.Network);

        Assert.Equal(ManifestSanitizer.MaxNetworkApps, doc.Apps.Count);
    }

    [Fact]
    public void ValidStoreAndWingetEntriesUntouched()
    {
        var doc = Doc(
            new AppRecord { Name = "Git", Id = "Git.Git", Source = "winget", InstallType = "winget" },
            new AppRecord { Name = "Apple Devices", Id = "9NP83LWLPZ9K", Source = "msstore", InstallType = "msstore" });

        Assert.Empty(ManifestSanitizer.Sanitize(doc, ManifestTrust.Network));
        Assert.Equal("winget", doc.Apps[0].InstallType);
        Assert.Equal("msstore", doc.Apps[1].InstallType);
    }
}

public sealed class DefaultManifestTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teachest-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Touch(string name)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, "{}");
        return path;
    }

    [Fact]
    public void FindsNothingInAnEmptyFolder() => Assert.Null(ManifestService.FindDefault(_directory));

    [Fact]
    public void StillFindsAnOldAppsJson() => Assert.Equal(Touch("apps.json"), ManifestService.FindDefault(_directory));

    [Fact]
    public void PrefersTeachestJson()
    {
        Touch("apps.json");
        Assert.Equal(Touch("teachest.json"), ManifestService.FindDefault(_directory));
    }
}
