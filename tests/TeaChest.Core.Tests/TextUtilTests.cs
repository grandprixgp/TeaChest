using TeaChest.Models;

namespace TeaChest.Tests;

public class TextUtilTests
{
    [Fact]
    public void KeepsChineseNameInStrictForm()
    {
        Assert.Equal("微信", TextUtil.NormalizeStrict("微信"));
    }

    [Fact]
    public void DistinguishesChineseNamesThatShareATrailingNumber()
    {
        Assert.NotEqual(TextUtil.NormalizeStrict("微信 3"), TextUtil.NormalizeStrict("钉钉 3"));
    }

    [Fact]
    public void LowercasesCyrillicAndDropsSpaces()
    {
        Assert.Equal("яндексбраузер", TextUtil.NormalizeStrict("Яндекс Браузер"));
    }

    [Fact]
    public void KeepsUmlauts()
    {
        Assert.Equal("müllertool", TextUtil.NormalizeStrict("Müller Tool"));
    }

    [Theory]
    [InlineData("กิน", "กัน")]
    [InlineData("कल", "काल")]
    public void KeepsWordsApartWhenOnlyTheirVowelMarksDiffer(string first, string second)
    {
        Assert.NotEqual(TextUtil.NormalizeStrict(first), TextUtil.NormalizeStrict(second));
        Assert.NotEqual(TextUtil.NormalizeLoose(first), TextUtil.NormalizeLoose(second));
    }

    [Fact]
    public void KeepsNonLatinNamesApartInLooseFormToo()
    {
        Assert.NotEqual(TextUtil.NormalizeLoose("微信 3"), TextUtil.NormalizeLoose("钉钉 3"));
    }

    [Theory]
    [InlineData("Microsoft Teams", "teams")]
    [InlineData("App (x64)", "app")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void AsciiStrictResultsAreUnchanged(string? input, string expected)
    {
        Assert.Equal(expected, TextUtil.NormalizeStrict(input));
    }

    [Fact]
    public void StrictKeepsVersionDigits()
    {
        Assert.Equal("python312", TextUtil.NormalizeStrict("Python 3.12"));
    }

    [Fact]
    public void LooseDropsVersionDigits()
    {
        Assert.Equal("python", TextUtil.NormalizeLoose("Python 3.12"));
    }

    [Fact]
    public void CleanAsciiStillDropsNonAsciiForIds()
    {
        Assert.Equal("", TextUtil.CleanAscii("微信"));
        Assert.Equal("Tool", TextUtil.CleanAscii("Tool 微信"));
    }
}
