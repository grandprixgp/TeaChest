using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class VsCodeSourceTests
{
    private const string Listing =
        "ms-python.python@2026.1.0\r\n" +
        "esbenp.prettier-vscode@11.0.0\r\n" +
        "ms-vscode-remote.remote-ssh@0.120.0\r\n";

    // Stands in for the shim runner, so no test ever starts the real code command.
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

    private static (VsCodeSource Source, FakeRunner Runner) Make(int exitCode, string output, bool timedOut = false)
    {
        var runner = new FakeRunner(new ProcessResult(exitCode, output, timedOut));
        return (new VsCodeSource(runner.RunAsync), runner);
    }

    [Fact]
    public void ParsesIdAndVersion()
    {
        List<AppItem> items = VsCodeSource.Parse(Listing);

        Assert.Equal(new[] { "ms-python.python", "esbenp.prettier-vscode", "ms-vscode-remote.remote-ssh" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "2026.1.0", "11.0.0", "0.120.0" }, items.Select(i => i.Version));
    }

    [Fact]
    public void NoteHoldsTheReinstallCommand()
    {
        AppItem item = Assert.Single(VsCodeSource.Parse("ms-python.python@2026.1.0"));

        Assert.Equal("code --install-extension ms-python.python", item.Notes);
        Assert.Equal("Development", item.Category);
    }

    [Theory]
    [InlineData("nodot@1.0.0", "nodot")]
    [InlineData("a.b.c@1.0.0", "a.b.c")]
    [InlineData("pub.na&me@1.0.0", "&")]
    [InlineData("pub.na%PATH%me@1.0.0", "%")]
    [InlineData("pub.name|calc@1.0.0", "|")]
    [InlineData("-.x@1.0.0", "")]
    [InlineData("--force.x@1.0.0", "--force")]
    public void OddIdsStayOutOfTheCommand(string line, string marker)
    {
        AppItem item = Assert.Single(VsCodeSource.Parse(line));

        Assert.DoesNotContain("code --install-extension", item.Notes);
        if (marker.Length > 0) Assert.DoesNotContain(marker, item.Notes);
    }

    [Fact]
    public void LinesThatAreNotExtensionsAreSkipped()
    {
        string output =
            "(node:1234) Warning: something deprecated\n" +
            "\n" +
            "Extensions installed on WSL: Ubuntu:\n" +
            "ms-python.python@2026.1.0\n" +
            "@1.0.0\n" +
            "dangling.id@\n" +
            "two.ats@1@2\n" +
            "has space.x@1.0\n" +
            "golang.go@0.48.0\n";

        List<AppItem> items = VsCodeSource.Parse(output);

        Assert.Equal(new[] { "ms-python.python", "golang.go" }, items.Select(i => i.Name));
    }

    [Fact]
    public void TruncatedOutputKeepsWhatParsedSoFar()
    {
        List<AppItem> items = VsCodeSource.Parse("ms-python.python@2026.1.0\nesbenp.prettier-vs");

        AppItem item = Assert.Single(items);
        Assert.Equal("ms-python.python", item.Name);
    }

    [Fact]
    public void DuplicatesAreListedOnce()
    {
        List<AppItem> items = VsCodeSource.Parse("ms-python.python@2026.1.0\nMS-Python.Python@2026.1.0\n");

        Assert.Single(items);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\r\n\r\n")]
    public void NothingGivesNothing(string? output) => Assert.Empty(VsCodeSource.Parse(output));

    [Fact]
    public async Task RunsCodeWithConstantArguments()
    {
        (VsCodeSource source, FakeRunner runner) = Make(0, Listing);

        await source.ScanAsync(CancellationToken.None);

        (string command, string[] args) = Assert.Single(runner.Calls);
        Assert.Equal("code", command);
        Assert.Equal(new[] { "--list-extensions", "--show-versions" }, args);
        Assert.True(ExtraSourceTools.IsSafeToken(command));
        Assert.All(args, a => Assert.True(ExtraSourceTools.IsSafeToken(a)));
    }

    [Fact]
    public async Task MissingCodeMeansNoExtensions()
    {
        (VsCodeSource source, _) = Make(-1, "code isn't installed.");

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutIsAnError()
    {
        (VsCodeSource source, _) = Make(-1, "", timedOut: true);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFailingRunIsAnError()
    {
        (VsCodeSource source, _) = Make(1, "ms-python.python@2026.1.0\nsomething broke");

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ACancelledScanStopsInsteadOfReportingNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (VsCodeSource source, _) = Make(-1, "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ScanAsync(cts.Token));
    }

    [Fact]
    public async Task AFailingRunBecomesAWarningNotAFailedScan()
    {
        (VsCodeSource source, _) = Make(1, "");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Contains(warnings, w => w.StartsWith("VS Code couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RowsComeOutLabelledAndUnticked()
    {
        (VsCodeSource source, _) = Make(0, "ms-python.python@2026.1.0\n");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        AppItem item = Assert.Single(items.Values);
        // TC-18's rule: the Source is the Name lower-cased, which AddOnSources already lists.
        Assert.Equal("VS Code: ms-python.python", item.Name);
        Assert.Equal("vs code", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal("2026.1.0", item.Version);
        Assert.Equal("code --install-extension ms-python.python", item.Notes);
        Assert.True(AppClassifier.IsAddOn(item));

        AppClassifier.ApplyDefaults(item, new SelectionOptions { IncludeAll = true });

        Assert.False(item.IsSelected);
        Assert.Equal("Development", item.Category);
        Assert.Equal("code --install-extension ms-python.python", item.Notes);
    }
}
