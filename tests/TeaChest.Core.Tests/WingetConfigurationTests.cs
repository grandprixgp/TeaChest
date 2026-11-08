using System.IO;
using System.Text;
using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public class WingetConfigurationTests
{
    private const string Sample =
        "# yaml-language-server: $schema=https://aka.ms/configuration-dsc-schema/0.2\n" +
        "# Made by TeaChest. Run it with: winget configure -f <this file>\n" +
        "properties:\n" +
        "  configurationVersion: 0.2.0\n" +
        "  resources:\n" +
        "    - resource: Microsoft.WinGet.DSC/WinGetPackage\n" +
        "      id: 'Git.Git'\n" +
        "      directives:\n" +
        "        description: 'Git'\n" +
        "      settings:\n" +
        "        id: 'Git.Git'\n" +
        "        source: winget\n";

    private static AppRecord App(string name, string id, string installType, bool include = true)
        => new() { Name = name, Id = id, InstallType = installType, Include = include };

    // Pulls the single-quoted description back out the way a YAML parser would.
    private static string ParsedDescription(string text)
    {
        string line = Assert.Single(text.Split('\n'), l => l.Contains("description:"));
        string raw = line["        description: ".Length..];
        Assert.StartsWith("'", raw);
        Assert.EndsWith("'", raw);

        string inner = raw[1..^1];
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\'') continue;
            Assert.True(i + 1 < inner.Length && inner[i + 1] == '\'', "an apostrophe was left half-escaped");
            i++;
        }
        return inner.Replace("''", "'");
    }

    [Fact]
    public void WritesTheSampleExactly()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App("Git", "Git.Git", "winget") });

        Assert.Equal(Sample, text);
    }

    [Fact]
    public void UsesUnixLineEndingsAndAFinalNewline()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App("Git", "Git.Git", "winget") });

        Assert.NotNull(text);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void KeepsAnIdWithPlusesIntact()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App("simplewall", "Henry++.simplewall", "winget") });

        Assert.NotNull(text);
        Assert.Contains("      id: 'Henry++.simplewall'\n", text);
        Assert.Contains("        id: 'Henry++.simplewall'\n", text);
    }

    [Fact]
    public void QuotesAndFlattensAnAwkwardName()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App("Bob's \"App\"\tline\nnext", "Vendor.App", "winget") });

        Assert.NotNull(text);
        Assert.Contains("        description: 'Bob''s \"App\" line next'\n", text);
        Assert.Single(text.Split('\n'), line => line.Contains("description:"));
    }

    [Fact]
    public void SkipsAppsThatCannotBeInstalledByWingetConfigure()
    {
        AppRecord[] apps =
        {
            App("Wanted", "Vendor.Wanted", "winget"),
            App("Not included", "Vendor.Unchecked", "winget", include: false),
            App("Manual", "Vendor.Manual", "manual"),
            App("Download", "Vendor.Download", "download"),
            App("Bad id", "--override.x", "winget"),
            App("Store id as winget", "9NP83LWLPZ9K", "winget")
        };

        string? text = WingetConfigurationWriter.Build(apps);

        Assert.NotNull(text);
        Assert.Contains("Vendor.Wanted", text);
        Assert.DoesNotContain("Vendor.Unchecked", text);
        Assert.DoesNotContain("Vendor.Manual", text);
        Assert.DoesNotContain("Vendor.Download", text);
        Assert.DoesNotContain("--override", text);
        Assert.DoesNotContain("9NP83LWLPZ9K", text);
    }

    [Fact]
    public void AStoreAppUsesTheStoreSource()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App("Apple Devices", "9NP83LWLPZ9K", "msstore") });

        Assert.NotNull(text);
        Assert.Contains("      id: '9NP83LWLPZ9K'\n", text);
        Assert.Contains("        id: '9NP83LWLPZ9K'\n", text);
        Assert.Contains("        source: msstore\n", text);
    }

    [Fact]
    public void DuplicateIdsAppearOnceKeepingTheFirst()
    {
        AppRecord[] apps =
        {
            App("Git", "Git.Git", "winget"),
            App("Git again", "git.git", "winget"),
            App("Git third", "GIT.GIT", "winget")
        };

        string? text = WingetConfigurationWriter.Build(apps);

        Assert.NotNull(text);
        Assert.Equal(1, text.Split("WinGetPackage").Length - 1);
        Assert.Contains("description: 'Git'\n", text);
        Assert.DoesNotContain("Git again", text);
    }

    [Fact]
    public void NothingQualifyingReturnsNull()
    {
        Assert.Null(WingetConfigurationWriter.Build(Array.Empty<AppRecord>()));
        Assert.Null(WingetConfigurationWriter.Build(new[] { App("Manual", "Vendor.App", "manual") }));
    }

    [Fact]
    public void CapsALongNameAtTheSchemaLimit()
    {
        string? text = WingetConfigurationWriter.Build(new[] { App(new string('n', 600), "Vendor.App", "winget") });

        Assert.NotNull(text);
        string description = ParsedDescription(text);
        Assert.True(description.Length <= 512, $"description was {description.Length} characters");
        Assert.EndsWith("…", description);
    }

    [Fact]
    public void CutsALongNameWithoutLeavingAHalfEscapedApostrophe()
    {
        // 600 characters, with an apostrophe every six.
        string name = string.Concat(Enumerable.Repeat("Bob's ", 100));

        string? text = WingetConfigurationWriter.Build(new[] { App(name, "Vendor.App", "winget") });

        Assert.NotNull(text);
        string description = ParsedDescription(text);
        Assert.True(description.Length <= 512, $"description was {description.Length} characters");
        Assert.Contains("Bob's", description);
    }

    [Fact]
    public void SkipsAnIdLongerThanTheSchemaLimit()
    {
        string id = new string('a', 32) + "." + new string('b', 32) + "." + new string('c', 32) + "." + new string('d', 30);

        Assert.Equal(129, id.Length);
        Assert.True(PackageIds.IsWingetId(id));

        Assert.Null(WingetConfigurationWriter.Build(new[] { App("Long id", id, "winget") }));
    }

    [Fact]
    public void KeepsAnIdAtExactlyTheSchemaLimit()
    {
        string id = new string('a', 32) + "." + new string('b', 32) + "." + new string('c', 32) + "." + new string('d', 29);

        Assert.Equal(128, id.Length);

        string? text = WingetConfigurationWriter.Build(new[] { App("Long id", id, "winget") });

        Assert.NotNull(text);
        Assert.Contains($"      id: '{id}'\n", text);
    }
}

public sealed class WingetConfigurationSaveTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("teachest-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string PathFor(string name) => Path.Combine(_directory, name);

    [Fact]
    public async Task SaveWritesAllFourFiles()
    {
        var service = new ManifestService();
        string path = PathFor("teachest.json");

        await service.SaveAsync(path, new[]
        {
            new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget", Source = "winget", IsSelected = true }
        });

        Assert.True(File.Exists(PathFor("teachest.json")));
        Assert.True(File.Exists(PathFor("teachest.csv")));
        Assert.True(File.Exists(PathFor("teachest.md")));
        Assert.True(File.Exists(PathFor("teachest.winget")));

        byte[] bytes = await File.ReadAllBytesAsync(PathFor("teachest.winget"));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        Assert.Contains("Git.Git", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public async Task ASaveWithNothingQualifyingRemovesTheStaleFile()
    {
        var service = new ManifestService();
        string path = PathFor("teachest.json");

        await service.SaveAsync(path, new[]
        {
            new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget", Source = "winget", IsSelected = true }
        });
        Assert.True(File.Exists(PathFor("teachest.winget")));

        await service.SaveAsync(path, new[]
        {
            new AppItem { Name = "Manual app", Id = "", InstallType = "manual", Source = "registry", IsSelected = true }
        });

        Assert.False(File.Exists(PathFor("teachest.winget")));
        Assert.True(File.Exists(PathFor("teachest.json")));
        Assert.True(File.Exists(PathFor("teachest.csv")));
        Assert.True(File.Exists(PathFor("teachest.md")));
    }
}
