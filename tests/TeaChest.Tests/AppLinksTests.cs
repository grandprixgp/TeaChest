using System.IO;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

public sealed class AppLinksTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("teachest-tests-");

    public void Dispose() => _temp.Delete(recursive: true);

    private static bool NoFolderCheck(string path) => throw new InvalidOperationException("The disk should not be touched for " + path);

    [Fact]
    public void StoreLinkUsesTheTemplate()
        => Assert.Equal("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1", AppLinks.StoreLink("9NBLGGH4NNS1"));

    [Fact]
    public void StoreLinkUpperCasesTheValidatedId()
        => Assert.Equal("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1", AppLinks.StoreLink("9nblggh4nns1"));

    [Fact]
    public void WingetLinkSplitsAtTheFirstDot()
        => Assert.Equal("https://winget.run/pkg/Microsoft/VisualStudioCode", AppLinks.WingetLink("Microsoft.VisualStudioCode"));

    [Fact]
    public void WingetLinkKeepsLaterDotsInTheName()
        => Assert.Equal("https://winget.run/pkg/Microsoft/DotNet.SDK.8", AppLinks.WingetLink("Microsoft.DotNet.SDK.8"));

    [Fact]
    public void WingetLinkEscapesPlus()
        => Assert.Equal("https://winget.run/pkg/Notepad%2B%2B/Notepad%2B%2B", AppLinks.WingetLink("Notepad++.Notepad++"));

    [Fact]
    public void WingetLinkEscapesCharactersThatWouldChangeTheUrl()
        => Assert.Equal("https://winget.run/pkg/Pub%23x/Name%26y%3Dz", AppLinks.WingetLink("Pub#x.Name&y=z"));

    [Theory]
    [InlineData("--override.x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("javascript:alert(1).x")]
    [InlineData("https://example.com/x")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("Foo.Bar/../x")]
    [InlineData("Foo..Bar")]
    [InlineData("NoDotAtAll")]
    [InlineData("Foo .Bar")]
    [InlineData("")]
    [InlineData(null)]
    public void UnsafeOrInvalidIdsGetNoLink(string? id)
    {
        Assert.Null(AppLinks.WingetLink(id));
        Assert.Null(AppLinks.StoreLink(id));
    }

    [Theory]
    [InlineData("9NBLGGH4NNS")]
    [InlineData("9NBLGGH4NNS12")]
    [InlineData("9NBLGGH4NN-1")]
    public void StoreLinkNeedsTwelveLettersOrDigits(string id)
        => Assert.Null(AppLinks.StoreLink(id));

    [Fact]
    public void WingetLinkIsOnlyForWingetRows()
    {
        var addOn = new AppItem { Name = "VS Code: ms-python.python", Id = "ms-python.python", InstallType = "manual" };
        var package = new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget" };

        Assert.Null(AppLinks.WingetLink(addOn));
        Assert.Equal("https://winget.run/pkg/Git/Git", AppLinks.WingetLink(package));
    }

    [Fact]
    public void ManifestUrlsAreNeverUsed()
    {
        // Every text field holds a URL, but none is a valid ID, so nothing comes back.
        var app = new AppItem
        {
            Name = "https://example.com/name",
            Id = "https://example.com/id",
            InstallType = "winget",
            Source = "https://example.com/source",
            Notes = "https://example.com/notes",
            DownloadUrl = "https://example.com/setup.exe",
            StoreSearchName = "ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1",
            InstallLocation = "https://example.com/folder"
        };

        Assert.Null(AppLinks.StoreLink(app));
        Assert.Null(AppLinks.WingetLink(app));
        Assert.Null(AppLinks.LocalFolder(app));
    }

    [Fact]
    public void LocalFolderReturnsAnExistingFolderWithATrailingSeparator()
    {
        string? folder = AppLinks.LocalFolder(_temp.FullName, Directory.Exists);

        Assert.Equal(Path.TrimEndingDirectorySeparator(_temp.FullName) + Path.DirectorySeparatorChar, folder);
    }

    [Fact]
    public void LocalFolderTrimsQuotesAndNormalises()
        => Assert.Equal(@"C:\Program Files\Foo\", AppLinks.LocalFolder("  \"C:/Program Files/Foo\"  ", _ => true));

    [Fact]
    public void LocalFolderSkipsAMissingFolder()
        => Assert.Null(AppLinks.LocalFolder(Path.Combine(_temp.FullName, "gone"), Directory.Exists));

    [Fact]
    public void LocalFolderSkipsAFile()
    {
        string file = Path.Combine(_temp.FullName, "setup.exe");
        File.WriteAllText(file, "");

        Assert.Null(AppLinks.LocalFolder(file, Directory.Exists));
    }

    [Theory]
    [InlineData(@"\\server\share\app")]
    [InlineData(@"//server/share/app")]
    [InlineData(@"\\?\C:\Program Files\Foo")]
    [InlineData(@"\\.\C:\Program Files\Foo")]
    [InlineData(@"C:relative\Foo")]
    [InlineData(@"relative\Foo")]
    [InlineData(@"\Program Files\Foo")]
    [InlineData(@"C:\Foo:stream")]
    [InlineData("file:///C:/Program%20Files")]
    [InlineData("1:\\Foo")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void LocalFolderRejectsAnythingButALocalDrivePathWithoutCheckingIt(string? location)
        => Assert.Null(AppLinks.LocalFolder(location, NoFolderCheck));

    [Fact]
    public void DescribeMatchCoversEachConfidence()
    {
        Assert.Equal("A loose name match against WinGet. Worth a quick check.",
            MainViewModel.DescribeMatch(new AppItem { Id = "Zoom.Zoom", Source = "winget", MatchConfidence = MatchConfidence.Loose }));
        Assert.Equal("An exact name match against the Microsoft Store.",
            MainViewModel.DescribeMatch(new AppItem { Id = "9NBLGGH4NNS1", Source = "msstore", MatchConfidence = MatchConfidence.Exact }));
        Assert.Equal("The ID came straight from WinGet, with no name guess involved.",
            MainViewModel.DescribeMatch(new AppItem { Id = "Git.Git", Source = "winget" }));
        Assert.Equal("Found in the registry, with no package ID.",
            MainViewModel.DescribeMatch(new AppItem { Source = "registry" }));
        Assert.Equal("Found in vs code, with no package ID.",
            MainViewModel.DescribeMatch(new AppItem { Source = "vs code" }));
        Assert.Equal("Found in an unknown source, with no package ID.",
            MainViewModel.DescribeMatch(new AppItem()));
    }

    [Fact]
    public void DetailRowsSkipEmptyFieldsButAlwaysSayHowItWasMatched()
    {
        var app = new AppItem { Name = "Tool", Source = "registry", Version = "1.0", Notes = "  " };

        IReadOnlyList<DetailRow> rows = MainViewModel.DetailRows(app);

        Assert.Equal(new[] { "Type", "Version", "Source", "How it was matched" }, rows.Select(r => r.Label));
    }

    [Fact]
    public void DetailRowsShowTheIdInMonospace()
    {
        var app = new AppItem
        {
            Name = "Git", Id = "Git.Git", InstallType = "winget", Version = "2.45", Category = "Development",
            Publisher = "The Git Development Community", Scope = "machine", Source = "winget", Notes = "Matched to a WinGet package by name."
        };

        IReadOnlyList<DetailRow> rows = MainViewModel.DetailRows(app);

        Assert.Equal(new[] { "ID", "Type", "Version", "Category", "Publisher", "Scope", "Source", "How it was matched", "Notes" },
            rows.Select(r => r.Label));
        Assert.Equal(new DetailRow("ID", "Git.Git", true), rows[0]);
        Assert.All(rows.Skip(1), r => Assert.False(r.IsMono));
    }

    [Fact]
    public void SelectingARowSetsUpThePanel()
    {
        var viewModel = new MainViewModel();
        var app = new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget", Source = "winget", InstallLocation = _temp.FullName };

        viewModel.SelectedApp = app;

        Assert.True(viewModel.HasSelectedApp);
        Assert.True(viewModel.HasWingetLink);
        Assert.False(viewModel.HasStoreLink);
        Assert.True(viewModel.HasInstallFolder);
        Assert.NotEmpty(viewModel.SelectedDetails);

        viewModel.SelectedApp = null;

        Assert.False(viewModel.HasSelectedApp);
        Assert.False(viewModel.HasWingetLink);
        Assert.False(viewModel.HasInstallFolder);
        Assert.Empty(viewModel.SelectedDetails);
    }

    [Fact]
    public void CloseHidesThePanel()
    {
        var viewModel = new MainViewModel { IsDetailsOpen = true };

        viewModel.CloseDetailsCommand.Execute(null);

        Assert.False(viewModel.IsDetailsOpen);
    }
}
