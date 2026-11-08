using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class ScoopSourceTests
{
    private const string Export =
        "{\r\n" +
        "    \"buckets\": [\r\n" +
        "        { \"Name\": \"main\", \"Source\": \"https://github.com/ScoopInstaller/Main\", \"Updated\": \"2026-11-01T10:00:00\", \"Manifests\": 1400 }\r\n" +
        "    ],\r\n" +
        "    \"apps\": [\r\n" +
        "        { \"Name\": \"7zip\", \"Version\": \"24.0\", \"Source\": \"main\", \"Updated\": \"2026-10-01T09:00:00\", \"Info\": \"\" },\r\n" +
        "        { \"Name\": \"git\", \"Version\": \"2.45.0\", \"Source\": \"main\", \"Updated\": \"2026-10-02T09:00:00\", \"Info\": \"\" },\r\n" +
        "        { \"Name\": \"notepadplusplus\", \"Version\": \"8.6.9\", \"Source\": \"extras\", \"Updated\": \"2026-10-03T09:00:00\", \"Info\": \"Global install\" }\r\n" +
        "    ]\r\n" +
        "}\r\n";

    // Stands in for the shim runner, so no test ever starts the real scoop command.
    private sealed class FakeRunner
    {
        private readonly ProcessResult _result;

        public FakeRunner(ProcessResult result) => _result = result;

        public List<(string Command, string[] Args)> Calls { get; } = new();

        public Task<ProcessResult> RunAsync(string command, string[] args, CancellationToken cancellationToken)
        {
            Calls.Add((command, args));
            return Task.FromResult(_result);
        }
    }

    private static (ScoopSource Source, FakeRunner Runner) Make(int exitCode, string output, bool timedOut = false)
    {
        var runner = new FakeRunner(new ProcessResult(exitCode, output, timedOut));
        return (new ScoopSource(runner.RunAsync), runner);
    }

    private static string App(string name, string version = "1.0") =>
        $"{{\"apps\":[{{\"Name\":\"{name}\",\"Version\":\"{version}\",\"Source\":\"main\"}}]}}";

    [Fact]
    public void ParsesNameAndVersion()
    {
        List<AppItem> items = ScoopSource.Parse(Export);

        Assert.Equal(new[] { "7zip", "git", "notepadplusplus" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "24.0", "2.45.0", "8.6.9" }, items.Select(i => i.Version));
    }

    [Fact]
    public void NoteHoldsTheReinstallCommand()
    {
        AppItem item = ScoopSource.Parse(Export)[0];

        Assert.Equal("scoop install 7zip", item.Notes);
    }

    [Fact]
    public void TheCardsExampleParses()
    {
        AppItem item = Assert.Single(ScoopSource.Parse("{\"apps\":[{\"Name\":\"7zip\",\"Version\":\"24.0\",\"Source\":\"main\"}],\"buckets\":[{\"Name\":\"main\"}]}"));

        Assert.Equal("7zip", item.Name);
        Assert.Equal("24.0", item.Version);
    }

    [Theory]
    [InlineData("python3.12")]
    [InlineData("vcredist2022")]
    [InlineData("notepad++")]
    [InlineData("dotnet-sdk")]
    [InlineData("my_app")]
    public void OrdinaryNamesAreKept(string name)
    {
        AppItem item = Assert.Single(ScoopSource.Parse(App(name)));

        Assert.Equal("scoop install " + name, item.Notes);
    }

    [Theory]
    [InlineData("calc&whoami")]
    [InlineData("a|b")]
    [InlineData("a\\\"b")]
    [InlineData("a'b")]
    [InlineData("has space")]
    [InlineData("%PATH%")]
    [InlineData("a^b")]
    [InlineData("a>b")]
    [InlineData("--global")]
    [InlineData("-x")]
    [InlineData("main/git")]
    [InlineData("git\\nx")]
    [InlineData("")]
    public void HostileNamesAreDropped(string name)
    {
        Assert.Empty(ScoopSource.Parse(App(name)));
    }

    [Fact]
    public void AHostileNameDoesNotTakeTheOthersWithIt()
    {
        string json = "{\"apps\":[" +
                      "{\"Name\":\"7zip\",\"Version\":\"24.0\"}," +
                      "{\"Name\":\"x & del /q *\",\"Version\":\"1.0\"}," +
                      "{\"Name\":\"git\",\"Version\":\"2.45.0\"}]}";

        List<AppItem> items = ScoopSource.Parse(json);

        Assert.Equal(new[] { "7zip", "git" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.DoesNotContain("&", i.Notes));
    }

    [Fact]
    public void OlderPlainLineOutputGivesNothing()
    {
        string output =
            "7zip (v:24.0) [main]\r\n" +
            "git (v:2.45.0) [main]\r\n" +
            "notepadplusplus (v:8.6.9) *global* [extras]\r\n";

        Assert.Empty(ScoopSource.Parse(output));
    }

    [Fact]
    public void AWarningBeforeTheJsonIsSkipped()
    {
        List<AppItem> items = ScoopSource.Parse("WARN  Scoop was updated recently.\r\n" + Export);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public void TruncatedOutputKeepsWhatParsedSoFar()
    {
        string cut = Export[..(Export.IndexOf("\"notepadplusplus\"", StringComparison.Ordinal) + 8)];

        List<AppItem> items = ScoopSource.Parse(cut);

        Assert.Equal(new[] { "7zip", "git" }, items.Select(i => i.Name));
    }

    [Fact]
    public void GarbageMidwayKeepsWhatParsedSoFar()
    {
        string json = "{\"apps\":[{\"Name\":\"7zip\",\"Version\":\"24.0\"},{\"Name\":\"git\" oops }]}";

        AppItem item = Assert.Single(ScoopSource.Parse(json));
        Assert.Equal("7zip", item.Name);
    }

    [Fact]
    public void OddEntriesAreSkipped()
    {
        string json = "{\"apps\":[" +
                      "\"just a string\"," +
                      "{\"Version\":\"1.0\"}," +
                      "{\"Name\":42,\"Version\":\"1.0\"}," +
                      "{\"Name\":\"curl\",\"Version\":{\"major\":8},\"Extra\":[1,2,{\"x\":null}]}," +
                      "{\"Name\":\"jq\",\"Version\":\"1.7.1\"}]}";

        List<AppItem> items = ScoopSource.Parse(json);

        Assert.Equal(new[] { "curl", "jq" }, items.Select(i => i.Name));
        Assert.Equal("", items[0].Version);
    }

    [Fact]
    public void DuplicatesAreListedOnce()
    {
        string json = "{\"apps\":[{\"Name\":\"git\",\"Version\":\"2.45.0\"},{\"Name\":\"Git\",\"Version\":\"2.45.0\"}]}";

        Assert.Single(ScoopSource.Parse(json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    [InlineData("{}")]
    [InlineData("{\"apps\":[],\"buckets\":[]}")]
    [InlineData("{\"apps\":null}")]
    [InlineData("[]")]
    public void NothingGivesNothing(string? output) => Assert.Empty(ScoopSource.Parse(output));

    [Fact]
    public async Task RunsScoopWithConstantArguments()
    {
        (ScoopSource source, FakeRunner runner) = Make(0, Export);

        await source.ScanAsync(CancellationToken.None);

        (string command, string[] args) = Assert.Single(runner.Calls);
        Assert.Equal("scoop", command);
        Assert.Equal(new[] { "export" }, args);
        Assert.True(ExtraSourceTools.IsSafeToken(command));
        Assert.All(args, a => Assert.True(ExtraSourceTools.IsSafeToken(a)));
    }

    [Fact]
    public async Task MissingScoopMeansNoApps()
    {
        (ScoopSource source, _) = Make(-1, "scoop isn't installed.");

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutIsAnError()
    {
        (ScoopSource source, _) = Make(-1, "", timedOut: true);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailingRunIsAnError()
    {
        (ScoopSource source, _) = Make(1, Export);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledScanStopsInsteadOfReportingNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (ScoopSource source, _) = Make(-1, "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ScanAsync(cts.Token));
    }

    [Fact]
    public async Task AFailingRunBecomesAWarningNotAFailedScan()
    {
        (ScoopSource source, _) = Make(1, "");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Contains(warnings, w => w.StartsWith("Scoop couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowsComeOutLabelledAndUnticked()
    {
        (ScoopSource source, _) = Make(0, Export);
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        Assert.Equal(3, items.Count);
        AppItem item = Assert.Single(items.Values, i => i.Name == "Scoop: 7zip");
        // TC-18's rule: the Source is the Name lower-cased, which AddOnSources already lists.
        Assert.Equal("scoop", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal("24.0", item.Version);
        Assert.Equal("scoop install 7zip", item.Notes);
        Assert.True(AppClassifier.IsAddOn(item));

        AppClassifier.ApplyDefaults(item, new SelectionOptions { IncludeAll = true });

        Assert.False(item.IsSelected);
        Assert.Equal("scoop install 7zip", item.Notes);
    }

    [Fact]
    public async Task AScoopGitDoesNotMergeWithGit()
    {
        (ScoopSource source, _) = Make(0, App("git", "2.45.0"));
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase)
        {
            // MergeRange's key for "Git"; "Scoop: git" keys as "scoopgit", so the two stay apart.
            ["git"] = new AppItem { Name = "Git", Version = "2.45.0", Source = "registry" }
        };

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        Assert.Contains(items.Values, i => i.Name == "Scoop: git" && i.Source == "scoop");
        Assert.Contains(items.Values, i => i.Name == "Git" && i.Source == "registry");
    }
}
