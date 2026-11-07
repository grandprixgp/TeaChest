using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class ChocolateySourceTests
{
    private const string Listing =
        "chocolatey|2.3.0\r\n" +
        "chocolatey-core.extension|1.4.0\r\n" +
        "git|2.45.0\r\n" +
        "git.install|2.45.0\r\n" +
        "notepadplusplus|8.6.9\r\n";

    // Stands in for the shim runner, so no test ever starts the real choco command. It answers
    // --version and list separately, the way choco would.
    private sealed class FakeRunner
    {
        private readonly ProcessResult _version;
        private readonly ProcessResult _list;

        public FakeRunner(ProcessResult version, ProcessResult list)
        {
            _version = version;
            _list = list;
        }

        public List<(string Command, string[] Args)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string command, string[] args, CancellationToken cancellationToken)
        {
            Calls.Add((command, args));
            return Task.FromResult(args.Contains("--version") ? _version : _list);
        }
    }

    private static (ChocolateySource Source, FakeRunner Runner) Make(int exitCode, string output, bool timedOut = false, string version = "2.3.0")
    {
        var runner = new FakeRunner(new ProcessResult(0, version + "\r\n", false), new ProcessResult(exitCode, output, timedOut));
        return (new ChocolateySource(runner.RunAsync), runner);
    }

    [Fact]
    public void ParsesNameAndVersion()
    {
        List<AppItem> items = ChocolateySource.Parse(Listing);

        Assert.Equal(new[] { "chocolatey-core.extension", "git", "git.install", "notepadplusplus" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "1.4.0", "2.45.0", "2.45.0", "8.6.9" }, items.Select(i => i.Version));
    }

    [Fact]
    public void NoteHoldsTheReinstallCommand()
    {
        AppItem item = Assert.Single(ChocolateySource.Parse("git|2.45.0"));

        Assert.Equal("git", item.Name);
        Assert.Equal("choco install git", item.Notes);
    }

    [Theory]
    [InlineData("chocolatey|2.3.0")]
    [InlineData("Chocolatey|1.4.0")]
    public void ChocolateyItselfIsSkipped(string line) => Assert.Empty(ChocolateySource.Parse(line));

    [Theory]
    [InlineData("python3.12")]
    [InlineData("vcredist140")]
    [InlineData("notepadplusplus.install")]
    [InlineData("dotnet-8.0-sdk")]
    [InlineData("my_pkg")]
    [InlineData("c++")]
    public void OrdinaryNamesAreKept(string name)
    {
        AppItem item = Assert.Single(ChocolateySource.Parse(name + "|1.0.0"));

        Assert.Equal("choco install " + name, item.Notes);
    }

    [Theory]
    [InlineData("calc&whoami|1.0")]
    [InlineData("a\"b|1.0")]
    [InlineData("a'b|1.0")]
    [InlineData("has space|1.0")]
    [InlineData("%PATH%|1.0")]
    [InlineData("a^b|1.0")]
    [InlineData("a>b|1.0")]
    [InlineData("--force|1.0")]
    [InlineData("-y|1.0")]
    [InlineData("a|b|1.0")]
    public void HostileNamesAreDropped(string line)
    {
        Assert.Empty(ChocolateySource.Parse(line));
    }

    [Fact]
    public void AHostileNameDoesNotTakeTheOthersWithIt()
    {
        List<AppItem> items = ChocolateySource.Parse("git|2.45.0\nx & del /q *|1.0\n7zip|24.0\n");

        Assert.Equal(new[] { "git", "7zip" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.DoesNotContain("&", i.Notes));
    }

    [Fact]
    public void LinesThatAreNotPackagesAreSkipped()
    {
        string output =
            "Chocolatey v2.3.0\n" +
            "\n" +
            "git|2.45.0\n" +
            "|1.0\n" +
            "dangling|\n" +
            "spaced|1 2\n" +
            "4 packages installed.\n" +
            "7zip|24.0\n";

        List<AppItem> items = ChocolateySource.Parse(output);

        Assert.Equal(new[] { "git", "7zip" }, items.Select(i => i.Name));
    }

    [Fact]
    public void TruncatedOutputKeepsWhatParsedSoFar()
    {
        List<AppItem> items = ChocolateySource.Parse("git|2.45.0\n7zip|24.0\nnotepadplus");

        Assert.Equal(new[] { "git", "7zip" }, items.Select(i => i.Name));
    }

    [Fact]
    public void DuplicatesAreListedOnce()
    {
        Assert.Single(ChocolateySource.Parse("git|2.45.0\nGit|2.45.0\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    [InlineData("chocolatey|2.3.0\r\n")]
    public void NothingGivesNothing(string? output) => Assert.Empty(ChocolateySource.Parse(output));

    [Theory]
    [InlineData(0, "2.3.0", false)]
    [InlineData(0, "2.0.0\r\n", false)]
    [InlineData(0, "10.1.0", false)]
    [InlineData(0, "1.4.0", true)]
    [InlineData(0, "0.12.1", true)]
    [InlineData(0, "v1.4.0", true)]
    [InlineData(0, "garbage", false)]
    [InlineData(0, "", false)]
    [InlineData(1, "1.4.0", false)]
    public void TellsOldChocolateyApart(int exitCode, string output, bool legacy)
    {
        Assert.Equal(legacy, ChocolateySource.IsLegacy(new ProcessResult(exitCode, output, false)));
    }

    [Fact]
    public async Task RunsChocoWithConstantArguments()
    {
        (ChocolateySource source, FakeRunner runner) = Make(0, Listing);

        await source.ScanAsync(CancellationToken.None);

        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal("choco", c.Command));
        Assert.Equal(new[] { "--version" }, runner.Calls[0].Args);
        Assert.Equal(new[] { "list", "--limit-output" }, runner.Calls[1].Args);
        Assert.All(runner.Calls, c =>
        {
            Assert.True(ExtraSourceTools.IsSafeToken(c.Command));
            Assert.All(c.Args, a => Assert.True(ExtraSourceTools.IsSafeToken(a)));
        });
    }

    [Fact]
    public async Task OldChocolateyListsOnlyThisPc()
    {
        (ChocolateySource source, FakeRunner runner) = Make(0, "git|2.45.0\n", version: "1.4.0");

        AppItem item = Assert.Single(await source.ScanAsync(CancellationToken.None));

        Assert.Equal("git", item.Name);
        Assert.Equal(new[] { "list", "--local-only", "--limit-output" }, runner.Calls[1].Args);
    }

    [Fact]
    public async Task MissingChocoMeansNoPackages()
    {
        var runner = new FakeRunner(new ProcessResult(-1, "choco isn't installed.", false), new ProcessResult(0, Listing, false));
        var source = new ChocolateySource(runner.RunAsync);

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task NothingInstalledWithEnhancedExitCodesIsNotAnError()
    {
        (ChocolateySource source, _) = Make(2, "");

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutIsAnError()
    {
        (ChocolateySource source, _) = Make(-1, "", timedOut: true);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AVersionTimeoutIsAnError()
    {
        var runner = new FakeRunner(new ProcessResult(-1, "", true), new ProcessResult(0, Listing, false));
        var source = new ChocolateySource(runner.RunAsync);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailingRunIsAnError()
    {
        (ChocolateySource source, _) = Make(1, "git|2.45.0\nsomething broke");

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledScanStopsInsteadOfReportingNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var runner = new FakeRunner(new ProcessResult(-1, "", false), new ProcessResult(-1, "", false));
        var source = new ChocolateySource(runner.RunAsync);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ScanAsync(cts.Token));
    }

    [Fact]
    public async Task AFailingRunBecomesAWarningNotAFailedScan()
    {
        (ChocolateySource source, _) = Make(1, "");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Contains(warnings, w => w.StartsWith("Chocolatey couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowsComeOutLabelledAndUnticked()
    {
        (ChocolateySource source, _) = Make(0, "git|2.45.0\n");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        AppItem item = Assert.Single(items.Values);
        // TC-18's rule: the Source is the Name lower-cased, which AddOnSources already lists.
        Assert.Equal("Chocolatey: git", item.Name);
        Assert.Equal("chocolatey", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal("2.45.0", item.Version);
        Assert.Equal("choco install git", item.Notes);
        Assert.True(AppClassifier.IsAddOn(item));

        AppClassifier.ApplyDefaults(item, new SelectionOptions { IncludeAll = true });

        Assert.False(item.IsSelected);
        Assert.Equal("choco install git", item.Notes);
    }

    [Fact]
    public async Task AChocolateyGitDoesNotMergeWithGit()
    {
        (ChocolateySource source, _) = Make(0, "git|2.45.0\n");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase)
        {
            // MergeRange's key for "Git"; "Chocolatey: git" keys as "chocolateygit", so the two stay apart.
            ["git"] = new AppItem { Name = "Git", Version = "2.45.0", Source = "registry" }
        };

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        Assert.Contains(items.Values, i => i.Name == "Chocolatey: git" && i.Source == "chocolatey");
        Assert.Contains(items.Values, i => i.Name == "Git" && i.Source == "registry");
    }
}
