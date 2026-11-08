using TeaChest.Models;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

public class ManifestDiffTests
{
    private static ManifestDocument Doc(params AppRecord[] apps) => new() { Apps = apps.ToList() };

    private static AppRecord Winget(string name, string id, string version = "") =>
        new() { Name = name, Id = id, Version = version, Source = "winget", InstallType = "winget" };

    private static AppRecord Manual(string name, string version = "") =>
        new() { Name = name, Version = version, Source = "registry", InstallType = "manual" };

    [Fact]
    public void SortsAppsIntoMissingAlreadyAndOnlyHere()
    {
        ManifestDocument before = Doc(Winget("Git", "Git.Git"), Winget("7-Zip", "7zip.7zip"));
        ManifestDocument after = Doc(Winget("Git", "Git.Git"), Winget("Firefox", "Mozilla.Firefox"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("7zip.7zip", Assert.Single(diff.MissingHere).Id);
        Assert.Equal("Git.Git", Assert.Single(diff.AlreadyHere).Id);
        Assert.Equal("Mozilla.Firefox", Assert.Single(diff.OnlyHere).Id);
    }

    [Fact]
    public void IdenticalManifestsAreAllAlreadyHere()
    {
        ManifestDocument before = Doc(Winget("Git", "Git.Git"), Manual("Paint.NET"));
        ManifestDocument after = Doc(Winget("Git", "Git.Git"), Manual("Paint.NET"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
        Assert.Equal(2, diff.AlreadyHere.Count);
    }

    [Fact]
    public void TwoEmptyManifestsGiveEmptyLists()
    {
        InventoryDiffResult diff = InventoryDiff.CompareManifests(Doc(), Doc());

        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.AlreadyHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void SameAppWithANewVersionIsUnchanged()
    {
        ManifestDocument before = Doc(Winget("Python", "Python.Python.3.12", "3.12.1"));
        ManifestDocument after = Doc(Winget("Python", "Python.Python.3.12", "3.13.0"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("Python.Python.3.12", Assert.Single(diff.AlreadyHere).Id);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void ManualAppWithANewVersionIsUnchanged()
    {
        ManifestDocument before = Doc(Manual("Contoso Tool", "1.0"));
        ManifestDocument after = Doc(Manual("Contoso Tool", "2.0"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Single(diff.AlreadyHere);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void MatchesByIdWhenBothSidesHaveOne()
    {
        ManifestDocument before = Doc(Winget("Git", "Git.Git"));
        ManifestDocument after = Doc(Winget("Git for Windows", "Git.Git"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("Git.Git", Assert.Single(diff.AlreadyHere).Id);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void MatchesByStrictNameWhenThereIsNoId()
    {
        ManifestDocument before = Doc(Manual("Paint.NET"));
        ManifestDocument after = Doc(Manual("paint.net"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Single(diff.AlreadyHere);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void SameNameWithDifferentIdsIsTwoApps()
    {
        ManifestDocument before = Doc(Winget("Git", "Git.Git"));
        ManifestDocument after = Doc(Winget("Git", "Other.Git"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("Git.Git", Assert.Single(diff.MissingHere).Id);
        Assert.Equal("Other.Git", Assert.Single(diff.OnlyHere).Id);
        Assert.Empty(diff.AlreadyHere);
    }

    [Fact]
    public void IdOnTheOlderSideOnlyStillMatchesByName()
    {
        ManifestDocument before = Doc(Winget("Git", "Git.Git"));
        ManifestDocument after = Doc(Manual("Git"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("Git.Git", Assert.Single(diff.AlreadyHere).Id);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void IdOnTheNewerSideOnlyStillMatchesByName()
    {
        ManifestDocument before = Doc(Manual("Git"));
        ManifestDocument after = Doc(Winget("Git", "Git.Git"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal("Git", Assert.Single(diff.AlreadyHere).Name);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void TwoIdRowsCanMatchOneNoIdRowByName()
    {
        ManifestDocument before = Doc(Winget("Foo", "A.Foo"), Winget("Foo", "B.Foo"));
        ManifestDocument after = Doc(Manual("Foo"));

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.Equal(2, diff.AlreadyHere.Count);
        Assert.Empty(diff.MissingHere);
        Assert.Empty(diff.OnlyHere);
    }

    [Fact]
    public void KeepsTheManifestSelectionOnTheDiffedItems()
    {
        ManifestDocument before = Doc(new AppRecord { Name = "Git", Id = "Git.Git", Include = false });
        ManifestDocument after = Doc(new AppRecord { Name = "Firefox", Id = "Mozilla.Firefox", Include = true });

        InventoryDiffResult diff = InventoryDiff.CompareManifests(before, after);

        Assert.False(Assert.Single(diff.MissingHere).IsSelected);
        Assert.True(Assert.Single(diff.OnlyHere).IsSelected);
    }
}
