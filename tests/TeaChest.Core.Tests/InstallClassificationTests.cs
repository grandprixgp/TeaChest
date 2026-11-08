using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

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
