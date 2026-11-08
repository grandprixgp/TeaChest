using TeaChest.Models;

namespace TeaChest.Tests;

public class ClassifierTests
{
    [Theory]
    [InlineData("Paint.NET", "Utilities")]
    [InlineData("paint.net", "Utilities")]
    [InlineData("Microsoft .NET Runtime - 8.0.1", "Runtime")]
    [InlineData("Microsoft ASP.NET Core 8.0", "Runtime")]
    [InlineData(".NET Framework 4.8 SDK", "Runtime")]
    [InlineData("Microsoft.NET.Native.Runtime.2.2", "Runtime")]
    [InlineData("1Password", "Security")]
    [InlineData("Microsoft Word", "Productivity")]
    [InlineData("Obsidian", "Productivity")]
    [InlineData("OBS Studio", "Media")]
    [InlineData("Jobs Manager", "Utilities")]
    [InlineData("Lego Digital Designer", "Utilities")]
    [InlineData("ego", "Browser")]
    [InlineData("Opera GX", "Browser")]
    [InlineData("Operations Manager", "Utilities")]
    [InlineData("Legit Tool", "Utilities")]
    [InlineData("Git", "Development")]
    [InlineData("GitHub Desktop", "Development")]
    [InlineData("TortoiseGit", "Development")]
    [InlineData("Steamroller Tool", "Utilities")]
    [InlineData("Steam", "Gaming")]
    [InlineData("SteamVR", "Gaming")]
    [InlineData("Epic Games Launcher", "Gaming")]
    [InlineData("Battle.net", "Gaming")]
    [InlineData("Excellent Tool", "Utilities")]
    [InlineData("Microsoft Excel", "Productivity")]
    [InlineData("LibreOffice", "Productivity")]
    [InlineData("Foxit PDF Reader", "Productivity")]
    [InlineData("ImageGlass", "Media")]
    [InlineData("Mailbird", "Communication")]
    [InlineData("NordVPN", "Security")]
    [InlineData("Mozilla Firefox", "Browser")]
    [InlineData("Discord", "Communication")]
    [InlineData("Python 3.12", "Development")]
    [InlineData("Windows Terminal", "Development")]
    public void CategoryMatchesWholeWords(string name, string expected)
        => Assert.Equal(expected, AppClassifier.GetCategory(name, "manual"));

    [Fact]
    public void PaintNetStaysSelected()
    {
        var item = new AppItem { Name = "Paint.NET", InstallType = "manual", Source = "registry" };

        AppClassifier.ApplyDefaults(item, new SelectionOptions());

        Assert.Equal("Utilities", item.Category);
        Assert.True(item.IsSelected);
    }
}
