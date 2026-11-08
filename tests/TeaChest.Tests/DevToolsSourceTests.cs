using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.ExtraSources;

namespace TeaChest.Tests;

public sealed class DevToolsSourceTests
{
    private const string NpmListing = """
        {
          "version": "10.8.0",
          "name": "lib",
          "dependencies": {
            "corepack": { "version": "0.29.3", "overridden": false },
            "npm": { "version": "10.8.0", "overridden": false },
            "typescript": { "version": "5.4.0", "overridden": false },
            "@angular/cli": { "version": "18.0.1", "overridden": false },
            "pnpm": { "version": "9.1.0", "overridden": false }
          }
        }
        """;

    private const string PipxListing = """
        {
          "pipx_spec_version": "0.1",
          "venvs": {
            "black": {
              "metadata": {
                "injected_packages": {},
                "main_package": { "package": "black", "package_or_url": "black", "package_version": "24.1" },
                "python_version": "Python 3.12.1"
              }
            },
            "poetry": {
              "metadata": {
                "main_package": { "package": "poetry", "package_version": "1.8.2" }
              }
            }
          }
        }
        """;

    private const string DotnetListing =
        "Package Id           Version      Commands\r\n" +
        "--------------------------------------------------\r\n" +
        "dotnet-ef            8.0.0        dotnet-ef\r\n" +
        "dotnet-format        5.1.250801   dotnet-format\r\n" +
        "powershell           7.4.2        pwsh\r\n";

    // Stands in for the shim runner, so no test ever starts the real npm, pipx or dotnet.
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

    private static (IExtraSource Source, FakeRunner Runner) Make(string kind, int exitCode, string output, bool timedOut = false)
    {
        var runner = new FakeRunner(new ProcessResult(exitCode, output, timedOut));
        IExtraSource source = kind switch
        {
            "npm" => new NpmSource(runner.RunAsync),
            "pipx" => new PipxSource(runner.RunAsync),
            "dotnet" => new DotnetToolSource(runner.RunAsync),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return (source, runner);
    }

    private static string ListingFor(string kind) => kind switch
    {
        "npm" => NpmListing,
        "pipx" => PipxListing,
        _ => DotnetListing
    };

    private static List<AppItem> ParseWith(string kind, string? output) => kind switch
    {
        "npm" => NpmSource.Parse(output),
        "pipx" => PipxSource.Parse(output),
        _ => DotnetToolSource.Parse(output)
    };

    private static string NpmWith(string name) =>
        "{\"dependencies\":{\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\":{\"version\":\"1.0.0\"}}}";

    private static string PipxWith(string name) =>
        "{\"venvs\":{\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") +
        "\":{\"metadata\":{\"main_package\":{\"package_version\":\"1.0\"}}}}}";

    private static string DotnetWith(string name) =>
        "Package Id      Version      Commands\n" +
        "-------------------------------------------\n" +
        name + "      1.0.0        tool\n";

    // ---- npm ----

    [Fact]
    public void NpmReadsGlobalPackagesAndSkipsNpmAndCorepack()
    {
        List<AppItem> items = NpmSource.Parse(NpmListing);

        Assert.Equal(new[] { "typescript", "@angular/cli", "pnpm" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "5.4.0", "18.0.1", "9.1.0" }, items.Select(i => i.Version));
        Assert.Equal("npm install -g typescript", items[0].Notes);
        Assert.Equal("npm install -g @angular/cli", items[1].Notes);
    }

    [Fact]
    public void NpmWarningsBeforeTheJsonAreSkipped()
    {
        string output = "npm warn config global `--global`, `--local` are deprecated.\r\n" + NpmListing;

        Assert.Equal(3, NpmSource.Parse(output).Count);
    }

    [Fact]
    public void NpmWithNothingGlobalGivesNothing()
    {
        Assert.Empty(NpmSource.Parse("{\"name\":\"lib\",\"dependencies\":{}}"));
        Assert.Empty(NpmSource.Parse("{}"));
    }

    [Theory]
    [InlineData("@scope/name")]
    [InlineData("@my-org/some.tool")]
    [InlineData("left-pad")]
    [InlineData("lodash.merge")]
    [InlineData("node_modules_x")]
    public void NpmOrdinaryNamesAreKept(string name)
    {
        AppItem item = Assert.Single(NpmSource.Parse(NpmWith(name)));

        Assert.Equal("npm install -g " + name, item.Notes);
    }

    [Theory]
    [InlineData("@scope/")]
    [InlineData("@/name")]
    [InlineData("scope/name")]
    [InlineData("@a/b/c")]
    [InlineData("@-x/name")]
    [InlineData("@scope/-name")]
    [InlineData("@scope/a&b")]
    public void NpmBadScopesAreDropped(string name)
    {
        Assert.Empty(NpmSource.Parse(NpmWith(name)));
    }

    [Fact]
    public void NpmScopeIsOnlyAllowedForNpm()
    {
        Assert.Empty(PipxSource.Parse(PipxWith("@scope/name")));
        Assert.Empty(DotnetToolSource.Parse(DotnetWith("@scope/name")));
    }

    // ---- pipx ----

    [Fact]
    public void PipxReadsVenvsAndTheirVersions()
    {
        List<AppItem> items = PipxSource.Parse(PipxListing);

        Assert.Equal(new[] { "black", "poetry" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "24.1", "1.8.2" }, items.Select(i => i.Version));
        Assert.Equal("pipx install black", items[0].Notes);
    }

    [Fact]
    public void PipxVenvWithoutMetadataStillCountsWithNoVersion()
    {
        AppItem item = Assert.Single(PipxSource.Parse("{\"venvs\":{\"httpie\":{}}}"));

        Assert.Equal("httpie", item.Name);
        Assert.Equal("", item.Version);
    }

    [Fact]
    public void PipxWithNoVenvsGivesNothing()
    {
        Assert.Empty(PipxSource.Parse("{\"pipx_spec_version\":\"0.1\",\"venvs\":{}}"));
    }

    // ---- dotnet tool ----

    [Fact]
    public void DotnetReadsTheTableUnderTheDashedLine()
    {
        List<AppItem> items = DotnetToolSource.Parse(DotnetListing);

        Assert.Equal(new[] { "dotnet-ef", "dotnet-format", "powershell" }, items.Select(i => i.Name));
        Assert.Equal(new[] { "8.0.0", "5.1.250801", "7.4.2" }, items.Select(i => i.Version));
        Assert.Equal("dotnet tool install -g dotnet-ef", items[0].Notes);
    }

    [Fact]
    public void DotnetDoesNotNeedAnEnglishHeader()
    {
        string output =
            "Paket-ID        Version      Befehle\n" +
            "-------------------------------------------\n" +
            "dotnet-ef       8.0.0        dotnet-ef\n";

        Assert.Equal("dotnet-ef", Assert.Single(DotnetToolSource.Parse(output)).Name);
    }

    [Fact]
    public void DotnetSkipsTheFirstRunBanner()
    {
        string output =
            "Welcome to .NET 8.0!\n" +
            "---------------------\n" +
            "SDK Version: 8.0.300\n" +
            "\n" +
            "Telemetry\n" +
            "---------\n" +
            "The .NET tools collect usage data in order to help us improve your experience.\n" +
            "\n" +
            DotnetListing;

        Assert.Equal(3, DotnetToolSource.Parse(output).Count);
    }

    [Fact]
    public void DotnetBannerWithoutATableGivesNothing()
    {
        string output =
            "Welcome to .NET 8.0!\n" +
            "---------------------\n" +
            "SDK Version: 8.0.300\n" +
            "Read more about .NET CLI Tools telemetry: https://aka.ms/dotnet-cli-telemetry\n";

        Assert.Empty(DotnetToolSource.Parse(output));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("no table here\r\n")]
    [InlineData("Package Id      Version      Commands\r\n-------------------------------------------\r\n")]
    public void DotnetWithNothingGivesNothing(string? output) => Assert.Empty(DotnetToolSource.Parse(output));

    // ---- shared rules ----

    // Each one goes into a reinstall command shown in Notes, so anything cmd.exe treats specially,
    // or that would read as a flag, has to be dropped before it gets there.
    public static IEnumerable<object[]> HostileNames()
    {
        foreach (string kind in new[] { "npm", "pipx", "dotnet" })
        {
            foreach (string name in new[]
            {
                "calc&whoami", "a|b", "a\"b", "a'b", "has space", "%PATH%", "a^b", "a>b", "a<b",
                "-g", "--force", "name\n", "name\r", "a;b", "a`b", "a(b)", "..\\x", "a/b"
            })
            {
                yield return new object[] { kind, name };
            }
        }
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void HostileNamesAreDropped(string kind, string name)
    {
        string output = kind switch
        {
            "npm" => NpmWith(name),
            "pipx" => PipxWith(name),
            _ => DotnetWith(name)
        };

        List<AppItem> items = ParseWith(kind, output);

        // Dotnet's table splits on whitespace, so "name\r" arrives as the plain cell "name", which
        // is fine to keep. What matters is that nothing unsafe reaches the note.
        if (kind == "dotnet" && name.Any(char.IsWhiteSpace))
        {
            Assert.All(items, i => Assert.Matches(@"^\w[\w.\-]*\z", i.Name));
            return;
        }
        Assert.Empty(items);
    }

    [Fact]
    public void ATrailingNewlineInAJsonKeyIsDropped()
    {
        // JSON can carry a newline inside a key, which ^...$ alone would let through.
        Assert.Empty(NpmSource.Parse("{\"dependencies\":{\"typescript\\n\":{\"version\":\"5.4.0\"}}}"));
        Assert.Empty(PipxSource.Parse("{\"venvs\":{\"black\\n\":{}}}"));
    }

    [Fact]
    public void ADotnetRowWithASpaceInTheIdOnlyKeepsAPlainFirstWord()
    {
        List<AppItem> items = DotnetToolSource.Parse(DotnetWith("bad&id more"));

        Assert.Empty(items);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    [InlineData("dotnet")]
    public void AHostileNameDoesNotTakeTheOthersWithIt(string kind)
    {
        string output = kind switch
        {
            "npm" => "{\"dependencies\":{\"typescript\":{\"version\":\"5.4.0\"},\"x & del /q *\":{\"version\":\"1.0\"},\"pnpm\":{\"version\":\"9.1.0\"}}}",
            "pipx" => "{\"venvs\":{\"black\":{},\"x & del /q *\":{},\"poetry\":{}}}",
            _ => "Id   Version   Commands\n------------------------------\ndotnet-ef   8.0.0   dotnet-ef\nx&del   1.0   x\npwsh   7.4.2   pwsh\n"
        };

        List<AppItem> items = ParseWith(kind, output);

        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.DoesNotContain("&", i.Notes));
    }

    [Theory]
    [InlineData("npm", "{\"dependencies\":{\"typescript\":{\"version\":\"5.4.0\"},\"pnpm\":{\"version\":\"9.1.0\"},\"esl", 2)]
    [InlineData("npm", "{\"dependencies\":{\"typescript\":{\"version\":\"5.4.0\"},\"pnpm\":{\"vers", 1)]
    [InlineData("pipx", "{\"venvs\":{\"black\":{\"metadata\":{}},\"poetry\":{\"metadata\":{\"main_pa", 1)]
    [InlineData("dotnet", "Id   Version   Commands\n------------------------------\ndotnet-ef   8.0.0   dotnet-ef\npwsh   7.4.2   pwsh\ndotnet-fo", 2)]
    [InlineData("npm", "{\"dependencies\":{\"typescript\":{\"version\":\"5.4.0\"}}} trailing junk", 1)]
    [InlineData("npm", "not json at all", 0)]
    [InlineData("pipx", "{\"venvs\": [1, 2]}", 0)]
    public void TruncatedOrOddOutputKeepsWhatParsedSoFar(string kind, string output, int expected)
    {
        Assert.Equal(expected, ParseWith(kind, output).Count);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    [InlineData("dotnet")]
    public void DuplicatesAreListedOnce(string kind)
    {
        string output = kind switch
        {
            "npm" => "{\"dependencies\":{\"typescript\":{\"version\":\"5.4.0\"},\"TypeScript\":{\"version\":\"5.4.0\"}}}",
            "pipx" => "{\"venvs\":{\"black\":{},\"Black\":{}}}",
            _ => "Id   Version   Commands\n------------------------------\ndotnet-ef   8.0.0   a\nDotnet-EF   8.0.0   a\n"
        };

        Assert.Single(ParseWith(kind, output));
    }

    [Fact]
    public void AnOddVersionIsLeftBlank()
    {
        AppItem item = Assert.Single(NpmSource.Parse("{\"dependencies\":{\"typescript\":{\"version\":\"5.4 && calc\"}}}"));

        Assert.Equal("", item.Version);
    }

    [Theory]
    [InlineData("npm", "npm", new[] { "ls", "-g", "--depth=0", "--json" })]
    [InlineData("pipx", "pipx", new[] { "list", "--json" })]
    [InlineData("dotnet", "dotnet", new[] { "tool", "list", "-g" })]
    public async Task RunsTheToolWithConstantArguments(string kind, string command, string[] args)
    {
        (IExtraSource source, FakeRunner runner) = Make(kind, 0, ListingFor(kind));

        await source.ScanAsync(CancellationToken.None);

        (string Command, string[] Args) call = Assert.Single(runner.Calls);
        Assert.Equal(command, call.Command);
        Assert.Equal(args, call.Args);
        Assert.True(ExtraSourceTools.IsSafeToken(call.Command));
        Assert.All(call.Args, a => Assert.True(ExtraSourceTools.IsSafeToken(a)));
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    [InlineData("dotnet")]
    public async Task AMissingToolMeansNothing(string kind)
    {
        (IExtraSource source, _) = Make(kind, -1, kind + " isn't installed.");

        Assert.Empty(await source.ScanAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    [InlineData("dotnet")]
    public async Task ATimeoutIsAnError(string kind)
    {
        (IExtraSource source, _) = Make(kind, -1, "", timedOut: true);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    [InlineData("dotnet")]
    public async Task ACancelledScanStopsInsteadOfReportingNothing(string kind)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        (IExtraSource source, _) = Make(kind, -1, "");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ScanAsync(cts.Token));
    }

    [Fact]
    public async Task NpmProblemsInTheTreeStillGiveTheList()
    {
        // npm ls exits 1 for things like a missing peer dependency, but still prints everything.
        (IExtraSource source, _) = Make("npm", 1, NpmListing + "\nnpm error code ELSPROBLEMS\n");

        Assert.Equal(3, (await source.ScanAsync(CancellationToken.None)).Count);
    }

    [Theory]
    [InlineData("npm")]
    [InlineData("pipx")]
    public async Task AFailingRunWithNothingReadableIsAnError(string kind)
    {
        (IExtraSource source, _) = Make(kind, 1, "something broke");

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PipxFailingIsAnErrorEvenWithJson()
    {
        (IExtraSource source, _) = Make("pipx", 1, PipxListing);

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ScanAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DotnetWithoutAnSdkMeansNothingRatherThanAWarning()
    {
        (IExtraSource source, _) = Make("dotnet", 145,
            "The command could not be loaded, possibly because:\r\n  * You intended to execute a .NET SDK command:\r\n      No .NET SDKs were found.\r\n");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Empty(warnings);
    }

    [Fact]
    public async Task AFailingRunBecomesAWarningNotAFailedScan()
    {
        (IExtraSource source, _) = Make("pipx", 1, "");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), warnings, _ => { });

        Assert.Empty(items);
        Assert.Contains(warnings, w => w.StartsWith("pipx couldn't be read", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("npm", "npm: typescript", "npm", "5.4.0", "npm install -g typescript")]
    [InlineData("pipx", "pipx: black", "pipx", "24.1", "pipx install black")]
    [InlineData("dotnet", "dotnet tool: dotnet-ef", "dotnet tool", "8.0.0", "dotnet tool install -g dotnet-ef")]
    public async Task RowsComeOutLabelledAndUnticked(string kind, string name, string sourceKey, string version, string notes)
    {
        (IExtraSource source, _) = Make(kind, 0, ListingFor(kind));
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase);

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        AppItem item = items.Values.Single(i => i.Name == name);
        // TC-18's rule: the Source is the Name lower-cased, which AddOnSources already lists.
        Assert.Equal(sourceKey, item.Source);
        Assert.Equal("manual", item.InstallType);
        Assert.Equal(version, item.Version);
        Assert.Equal(notes, item.Notes);
        Assert.All(items.Values, i => Assert.True(AppClassifier.IsAddOn(i)));

        foreach (AppItem row in items.Values) AppClassifier.ApplyDefaults(row, new SelectionOptions { IncludeAll = true });

        Assert.All(items.Values, i => Assert.False(i.IsSelected));
        Assert.Equal(notes, item.Notes);
    }

    [Fact]
    public async Task AnNpmGitDoesNotMergeWithGit()
    {
        (IExtraSource source, _) = Make("npm", 0, "{\"dependencies\":{\"git\":{\"version\":\"0.1.5\"}}}");
        var items = new Dictionary<string, AppItem>(StringComparer.OrdinalIgnoreCase)
        {
            ["git"] = new AppItem { Name = "Git", Version = "2.45.0", Source = "registry" }
        };

        await ScanService.CollectAsync(new List<Task<ScanService.SourceBatch>> { ScanService.WrapExtraSource(source, CancellationToken.None) },
            items, new object(), new List<string>(), _ => { });

        Assert.Contains(items.Values, i => i.Name == "npm: git" && i.Source == "npm");
        Assert.Contains(items.Values, i => i.Name == "Git" && i.Source == "registry");
    }

    [Fact]
    public void TheThreeSourcesHaveDifferentNames()
    {
        var names = new IExtraSource[] { new NpmSource(), new PipxSource(), new DotnetToolSource() }
            .Select(s => s.Name.ToLowerInvariant())
            .ToList();

        Assert.Equal(new[] { "npm", "pipx", "dotnet tool" }, names);
        Assert.All(names, n => Assert.Contains(n, AppClassifier.AddOnSources));
    }
}
