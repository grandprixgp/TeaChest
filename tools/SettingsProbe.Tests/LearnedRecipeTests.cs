namespace SettingsProbe.Tests;

public class LearnedRecipeTests
{
    [Theory]
    [InlineData(@"Contoso\App\settings.json", "Offer")]
    [InlineData(@"Contoso\App\Profiles\default.ini", "Offer")]
    [InlineData(@"Code\User\keybindings.json", "Offer")]
    [InlineData(@"Contoso\App\Cache\thumb.bin", "Exclude")]
    [InlineData(@"Code\Code Cache\js\index", "Exclude")]
    [InlineData(@"Code\GPUCache\data_0", "Exclude")]
    [InlineData(@"Contoso\App\logs\app.log", "Exclude")]
    [InlineData(@"Contoso\App\crash.dmp", "Exclude")]
    [InlineData(@"Contoso\App\Crashpad\reports\x", "Exclude")]
    [InlineData(@"Contoso\App\token.json", "NeverCopy")]
    [InlineData(@"Slack\Cookies", "NeverCopy")]
    [InlineData(@"Slack\Network\Cookies", "NeverCopy")]
    [InlineData(@"Slack\Local Storage\leveldb\000003.log", "NeverCopy")]
    [InlineData(@"Slack\Local State", "NeverCopy")]
    [InlineData(@".ssh\id_ed25519", "NeverCopy")]
    [InlineData(@".git-credentials", "NeverCopy")]
    [InlineData(@"App\license.lic", "NeverCopy")]
    [InlineData(@"Packages\Contoso.App_8wekyb3d8bbwe\Settings\settings.dat", "NeverCopy")]
    public void Classifies_paths(string path, string expected)
    {
        Assert.Equal(Enum.Parse<Verdict>(expected), LearnedRecipe.Classify(path, out _));
    }

    [Fact]
    public void Words_that_only_contain_a_rule_word_are_not_caught()
    {
        // "logbook" isn't a logs folder, and "Cachet" isn't a cache.
        Assert.Equal(Verdict.Offer, LearnedRecipe.Classify(@"Logbook\entries.json", out _));
        Assert.Equal(Verdict.Offer, LearnedRecipe.Classify(@"Cachet\config.yaml", out _));
    }

    [Fact]
    public void Build_skips_folders_marks_transients_and_sorts_offers_first()
    {
        var seen = new[]
        {
            (@"App", true, false, false),
            (@"App\logs\app.log", false, false, false),
            (@"App\settings.json", false, false, false),
            (@"App\recover.tmp", false, true, false),
            (@"App\export.json", false, false, true),
        };

        IReadOnlyList<LearnedItem> items = LearnedRecipe.Build(seen);

        Assert.Equal(new[] { @"App\settings.json", @"App\logs\app.log", @"App\export.json", @"App\recover.tmp" }, items.Select(i => i.RelativePath));
        Assert.Equal(new[] { Verdict.Offer, Verdict.Exclude, Verdict.Transient, Verdict.Transient }, items.Select(i => i.Verdict));
    }
}
