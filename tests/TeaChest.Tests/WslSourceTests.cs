using System.IO;
using System.Text;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class WslSourceTests
{
    private const string Listing =
        "  NAME            STATE           VERSION\r\n" +
        "* Ubuntu          Running         2\r\n" +
        "  Debian          Stopped         2\r\n" +
        "  kali-linux      Stopped         1\r\n" +
        "  Ubuntu-22.04    Stopped         2\r\n";

    // The start of the listing exactly as it arrives: UTF-16LE read as UTF-8 leaves a NUL after
    // every character, and each line break's trailing NUL lands on a line of its own.
    private const string RawNulListing =
        " \0 \0N\0A\0M\0E\0 \0 \0 \0S\0T\0A\0T\0E\0 \0 \0 \0V\0E\0R\0S\0I\0O\0N\0\r\n" +
        "\0\r\n" +
        "\0*\0 \0U\0b\0u\0n\0t\0u\0 \0 \0 \0R\0u\0n\0n\0i\0n\0g\0 \0 \0 \02\0\r\n" +
        "\0\r\n" +
        "\0 \0 \0D\0e\0b\0i\0a\0n\0 \0 \0 \0S\0t\0o\0p\0p\0e\0d\0 \0 \0 \02\0\r\n" +
        "\0\r\n";

    /// <summary>
    /// Does to <paramref name="text"/> what really happens to wsl.exe's output: written as
    /// UTF-16LE, then read line by line as UTF-8 and joined, the way ProcessRunner does it.
    /// </summary>
    private static string AsProcessRunnerSeesIt(string text, bool byteOrderMark = false)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        if (byteOrderMark) bytes = Encoding.Unicode.GetPreamble().Concat(bytes).ToArray();
        using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        var output = new StringBuilder();
        while (reader.ReadLine() is { } line) output.AppendLine(line);
        return output.ToString();
    }

    // Stands in for the process runner, so no test ever starts the real wsl.exe.
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

    private static (WslSource Source, FakeRunner Runner) Make(int exitCode, string output, bool timedOut = false)
    {
        var runner = new FakeRunner(new ProcessResult(exitCode, output, timedOut));
        return (new WslSource(runner.RunAsync), runner);
    }

    [Fact]
    public void ParsesPlainOutput()
    {
        List<AppItem> items = WslSource.Parse(Listing);

        Assert.Equal(new[] { "Ubuntu", "Debian", "kali-linux", "Ubuntu-22.04" }, items.Select(i => i.Name));
        Assert.Equal("wsl --install -d Ubuntu", items[0].Notes);
        Assert.All(items, i => Assert.Equal("", i.Version));
    }

    [Fact]
    public void StripsTheNulsFromACannedString()
    {
        Assert.Contains('\0', RawNulListing);

        List<AppItem> items = WslSource.Parse(RawNulListing);

        Assert.Equal(new[] { "Ubuntu", "Debian" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.DoesNotContain('\0', i.Name + i.Notes));
    }

    [Fact]
    public void ReadsUtf16TheWayProcessRunnerDeliversIt()
    {
        string mangled = AsProcessRunnerSeesIt(Listing);
        Assert.Contains('\0', mangled);

        Assert.Equal(new[] { "Ubuntu", "Debian", "kali-linux", "Ubuntu-22.04" }, WslSource.Parse(mangled).Select(i => i.Name));
    }

    [Fact]
    public void AByteOrderMarkDoesNotGetInTheWay()
    {
        Assert.Equal(4, WslSource.Parse(AsProcessRunnerSeesIt(Listing, byteOrderMark: true)).Count);
    }

    [Fact]
    public void Utf8OutputStillWorks()
    {
        // WSL_UTF8=1 makes wsl.exe write UTF-8, which then arrives with no NULs at all.
        Assert.Equal(4, WslSource.Parse(Listing).Count);
    }

    [Fact]
    public void TheHeaderIsSkippedByPositionWhateverTheLanguage()
    {
        string output = AsProcessRunnerSeesIt(
            "  NAME            STATUS          VERSION\r\n" +
            "* Ubuntu          Wird ausgeführt 2\r\n" +
            "  Debian          Beendet         2\r\n");

        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslSource.Parse(output).Select(i => i.Name));
    }

    [Fact]
    public void AHeaderWithADigitAtTheEndIsStillSkipped()
    {
        // Skipped by position, so even a header that happens to look like a row never becomes one.
        Assert.Equal(new[] { "Debian" }, WslSource.Parse("  Name Zustand 2\r\n  Debian Stopped 2\r\n").Select(i => i.Name));
    }

    [Fact]
    public void AMessageBeforeTheTableIsNotARow()
    {
        string output = AsProcessRunnerSeesIt(
            "wsl: A localhost proxy configuration was detected but not mirrored into WSL.\r\n" +
            Listing);

        Assert.Equal(new[] { "Ubuntu", "Debian", "kali-linux", "Ubuntu-22.04" }, WslSource.Parse(output).Select(i => i.Name));
    }

    [Fact]
    public void TheDefaultMarkIsNotPartOfTheName()
    {
        AppItem item = Assert.Single(WslSource.Parse("NAME STATE VERSION\n*Ubuntu Running 2\n"));

        Assert.Equal("Ubuntu", item.Name);
    }

    [Theory]
    [InlineData("docker-desktop")]
    [InlineData("docker-desktop-data")]
    [InlineData("rancher-desktop")]
    [InlineData("Rancher-Desktop-Data")]
    public void DistrosOtherAppsMakeAreLeftOut(string name)
    {
        Assert.Empty(WslSource.Parse("NAME STATE VERSION\n  " + name + "  Stopped  2\n"));
    }

    [Theory]
    [InlineData("Ubuntu")]
    [InlineData("Ubuntu-24.04")]
    [InlineData("openSUSE-Tumbleweed")]
    [InlineData("OracleLinux_9_1")]
    [InlineData("kali-linux")]
    public void OrdinaryNamesAreKept(string name)
    {
        AppItem item = Assert.Single(WslSource.Parse(AsProcessRunnerSeesIt("NAME STATE VERSION\r\n  " + name + "  Stopped  2\r\n")));

        Assert.Equal("wsl --install -d " + name, item.Notes);
    }

    [Theory]
    [InlineData("calc&whoami")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    [InlineData("a'b")]
    [InlineData("%PATH%")]
    [InlineData("a^b")]
    [InlineData("a>b")]
    [InlineData("-d")]
    [InlineData("--unregister")]
    [InlineData("a/b")]
    [InlineData("a+b")]
    public void HostileNamesAreDropped(string name)
    {
        Assert.Empty(WslSource.Parse("NAME STATE VERSION\n  " + name + "  Stopped  2\n"));
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("name\n")]
    [InlineData("name\r")]
    public void ANameBrokenByWhitespaceNeverBecomesARow(string name)
    {
        // Split on whitespace, "has space" leaves "has" with a state of "space Stopped", and a
        // trailing newline leaves "name" on a line with no version, so neither can pass as a row
        // with the hostile part in it.
        List<AppItem> items = WslSource.Parse("NAME STATE VERSION\n  " + name + "  Stopped  2\n");

        Assert.All(items, i => Assert.Matches(@"^\w[\w.\-]*\z", i.Name));
        Assert.DoesNotContain(items, i => i.Name.Contains(' ') || i.Name.Contains('\n') || i.Name.Contains('\r'));
    }

    [Fact]
    public void AHostileNameDoesNotTakeTheOthersWithIt()
    {
        List<AppItem> items = WslSource.Parse(AsProcessRunnerSeesIt(
            "NAME STATE VERSION\r\n  Ubuntu Stopped 2\r\n  x&del Stopped 2\r\n  Debian Stopped 2\r\n"));

        Assert.Equal(new[] { "Ubuntu", "Debian" }, items.Select(i => i.Name));
        Assert.All(items, i => Assert.DoesNotContain("&", i.Notes));
    }

    [Fact]
    public void TruncatedOutputKeepsWhatParsedSoFar()
    {
        string output = AsProcessRunnerSeesIt(Listing);
        string cut = output[..(output.IndexOf("k\0a\0l\0i", StringComparison.Ordinal) + 6)];

        Assert.Equal(new[] { "Ubuntu", "Debian" }, WslSource.Parse(cut).Select(i => i.Name));
    }

    [Fact]
    public void DuplicatesAreListedOnce()
    {
        Assert.Single(WslSource.Parse("NAME STATE VERSION\n  Ubuntu Stopped 2\n  ubuntu Stopped 2\n"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\r\n\0\r\n")]
    [InlineData("  NAME            STATE           VERSION\r\n")]
    public void NothingGivesNothing(string? output) => Assert.Empty(WslSource.Parse(output));

    [Fact]
    public async Task RunsWslFromSystem32WithConstantArguments()
    {
        (WslSource source, FakeRunner runner) = Make(0, Listing);

        await source.ScanAsync(CancellationToken.None);

        (string command, string[] args) = Assert.Single(runner.Calls);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "wsl.exe"), command);
        Assert.True(Path.IsPathRooted(command));
        Assert.Equal(new[] { "--list", "--verbose" }, args);
        Assert.All(args, a => Assert.True(ExtraSourceTools.IsSafeToken(a)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    [InlineData(50)]
    public async Task ANonZeroExitMeansNoWsl(int exitCode)
    {
        // Even when the output looks like a list, as the card says: not 0 means no WSL.
        (WslSource source, _) = Make(exitCode, AsProcessRunnerSeesIt(Listing));

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoWslIsNotAWarning()
    {
        (WslSource source, _) = Make(1, AsProcessRunnerSeesIt("Windows Subsystem for Linux has no installed distributions.\r\n"));
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task ATimeoutIsAnError()
    {
        (WslSource source, _) = Make(-1, "", timedOut: true);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ATimeoutBecomesAWarningNotAFailedScan()
    {
        (WslSource source, _) = Make(-1, "", timedOut: true);
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Contains(warnings, w => w.StartsWith("WSL couldn't be read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACancelledScanStopsInsteadOfReportingNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (WslSource source, _) = Make(-1, "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ScanAsync(cts.Token));
    }

    [Fact]
    public async Task RowsComeOutLabelledAndUnticked()
    {
        (WslSource source, _) = Make(0, AsProcessRunnerSeesIt(Listing));
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        AppItem item = items.Values.Single(i => i.Name == "WSL: Ubuntu");
        // TC-18's rule: the Source is the Name lower-cased, which AddOnSources already lists.
        Assert.Equal("wsl", item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal("wsl --install -d Ubuntu", item.Notes);
        Assert.Equal(4, items.Count);
        Assert.All(items.Values, i => Assert.True(AppClassifier.IsAddOn(i)));

        foreach (AppItem row in items.Values) AppClassifier.ApplyDefaults(row, new SelectionOptions { IncludeAll = true });

        Assert.All(items.Values, i => Assert.False(i.IsSelected));
        Assert.Equal("wsl --install -d Ubuntu", item.Notes);
    }

    [Fact]
    public async Task AWslUbuntuDoesNotMergeWithUbuntu()
    {
        (WslSource source, _) = Make(0, Listing);
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase)
        {
            // The store app Ubuntu, as AppX would list it.
            ["ubuntu"] = new AppItem { Name = "Ubuntu", Version = "2204.3.63.0", Source = "msix" }
        };

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        Assert.Contains(items.Values, i => i.Name == "WSL: Ubuntu" && i.Source == "wsl");
        Assert.Contains(items.Values, i => i.Name == "Ubuntu" && i.Source == "msix");
    }
}
