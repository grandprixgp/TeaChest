using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TeaChest.Models;

namespace TeaChest.Services;

public sealed class InstallOptions
{
    public bool Force { get; set; }
    public bool Interactive { get; set; }
    public bool SkipStore { get; set; }
    public bool MatchScope { get; set; }
    public bool PinVersions { get; set; }
}

public sealed class InstallPlan
{
    public List<PlanItem> Items { get; } = new();
    public string? Warning { get; set; }
    public int InstallCount => Items.Count(i => i.Action == "Install");
    public int SkipCount => Items.Count(i => i.Action == "Skip");
    public int ManualCount => Items.Count(i => i.Action == "Manual");
}

public sealed class InstallResultRecord
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Status { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Code { get; set; } = "";
    public double Seconds { get; set; }
    public string Output { get; set; } = "";
}

public sealed class InstallReport
{
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.Now;
    public string Machine { get; set; } = Environment.MachineName;
    public int Installed { get; set; }
    public int RebootRequired { get; set; }
    public int AlreadyInstalled { get; set; }
    public int Manual { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }
    public int NotAttempted { get; set; }
    public List<InstallResultRecord> Results { get; set; } = new();
    public string LogPath { get; set; } = "";
    public string ReportPath { get; set; } = "";
    public string ManualActionsPath { get; set; } = "";
}

public sealed class InstallService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly IInstallBackend _backend;

    public InstallService(IInstallBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Passing a runner lets tests drive each app's outcome without starting a package manager.
    /// Only for InstallAsync: the plan this builds sees an empty installed index.
    /// </summary>
    internal InstallService(Func<AppItem, InstallOptions, Action<string>?, CancellationToken, Task<ProcessResult>> runInstall)
        : this(new RunnerOnlyBackend(runInstall))
    {
    }

    public async Task<InstallPlan> BuildPlanAsync(
        IReadOnlyList<AppItem> apps,
        InstallOptions options,
        CancellationToken cancellationToken)
    {
        var plan = new InstallPlan();
        InstalledIndex? installed = null;
        try
        {
            installed = await _backend.BuildInstalledIndexAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            plan.Warning = "Could not build the installed-app index; the plan may include already-installed apps. " + ex.Message;
        }

        foreach (AppItem app in apps.Where(a => a.IsSelected))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = new PlanItem { App = app, Name = app.Name, Id = app.Id, Type = app.InstallType };
            bool isStore = string.Equals(app.InstallType, "msstore", StringComparison.OrdinalIgnoreCase);
            if (app.IsManual)
            {
                item.Action = "Manual";
                item.Detail = string.IsNullOrWhiteSpace(app.Notes) ? "No automatic installer." : app.Notes;
            }
            else if (app.IsDownload)
            {
                item.Action = "Manual";
                item.Detail = "Download entries are not installed by this GUI build. Use the CLI installer for this item.";
            }
            else if (string.IsNullOrWhiteSpace(app.Id))
            {
                item.Action = "Manual";
                item.Detail = "Missing package ID.";
            }
            else if (isStore ? !PackageIds.IsStoreId(app.Id) : !PackageIds.IsWingetId(app.Id))
            {
                item.Action = "Manual";
                item.Detail = "The package ID is not valid, so it will not be passed to WinGet.";
            }
            else if (options.SkipStore && isStore)
            {
                item.Action = "Manual";
                item.Detail = "Microsoft Store installs are skipped.";
            }
            else if (!options.Force && installed != null && installed.Contains(app))
            {
                item.Action = "Skip";
                item.Detail = "Already installed.";
            }
            else
            {
                item.Action = "Install";
                item.Detail = isStore ? "Microsoft Store package." : "WinGet package.";
            }
            plan.Items.Add(item);
        }
        return plan;
    }

    /// <summary>
    /// Installs the plan's items one by one. Cancelling stops after the current app and still
    /// returns (and writes) a report covering everything attempted so far.
    /// </summary>
    // TODO: every machine-scope installer pops its own UAC prompt, which gets old fast with 40
    // apps. Offer a "restart as admin" button before a big batch.
    public async Task<InstallReport> InstallAsync(
        InstallPlan plan,
        InstallOptions options,
        string logDirectory,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(logDirectory);
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string logPath = Path.Combine(logDirectory, $"install-log-{stamp}.txt");
        string reportPath = Path.Combine(logDirectory, $"install-report-{stamp}.json");
        string manualPath = Path.Combine(logDirectory, $"manual-actions-{stamp}.txt");
        var results = new List<InstallResultRecord>();
        List<PlanItem> installItems = plan.Items.Where(i => i.Action == "Install").ToList();
        int total = installItems.Count;
        int index = 0;
        bool cancelled = false;

        // Written line by line so a crash or kill mid-install still leaves a usable log.
        await using var log = new StreamWriter(logPath, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var logGate = new object();
        void AddLog(string line)
        {
            lock (logGate) log.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
        }

        InstallReport report;
        try
        {
            foreach (PlanItem item in installItems)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
                index++;
                AppItem app = item.App;
                var stopwatch = Stopwatch.StartNew();
                var appOutput = new StringBuilder();
                var gate = new object();

                AddLog($"[{index}/{total}] {app.Name} ({app.InstallType})");
                progress?.Report(new InstallProgress
                {
                    Current = index,
                    Total = total,
                    AppName = app.Name,
                    Status = "Installing",
                    Item = item
                });

                ProcessResult result = await _backend.InstallAsync(
                    app,
                    options,
                    line =>
                    {
                        lock (gate) appOutput.AppendLine(line);
                        AddLog("  " + line);
                        progress?.Report(new InstallProgress
                        {
                            Current = index,
                            Total = total,
                            AppName = app.Name,
                            Status = "Installing",
                            Line = line,
                            Item = item
                        });
                    },
                    cancellationToken).ConfigureAwait(false);

                stopwatch.Stop();
                bool appCancelled = cancellationToken.IsCancellationRequested;
                (string status, string detail) = Classify(result, appCancelled);
                string code = ExitCode(result.ExitCode);

                AddLog($"  => {status}: {detail} ({code})");
                progress?.Report(new InstallProgress
                {
                    Current = index,
                    Total = total,
                    AppName = app.Name,
                    Status = status,
                    Detail = detail,
                    Item = item
                });

                results.Add(new InstallResultRecord
                {
                    Name = app.Name,
                    Id = app.Id,
                    Type = app.InstallType,
                    Status = status,
                    Detail = detail,
                    Code = code,
                    Seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                    Output = Tail(appOutput.ToString(), 12000)
                });

                if (appCancelled)
                {
                    cancelled = true;
                    break;
                }
            }
        }
        finally
        {
            report = BuildReport(results, plan, logPath, reportPath, manualPath);
            report.Cancelled = cancelled;
            report.NotAttempted = total - results.Count;
            if (cancelled) AddLog($"Cancelled. {report.NotAttempted} app(s) were not attempted.");

            report.ManualActionsPath = WriteManualActions(plan, report, manualPath) ? manualPath : "";
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, JsonOptions), new UTF8Encoding(false), CancellationToken.None).ConfigureAwait(false);
        }
        return report;
    }

    internal static (string Status, string Detail) Classify(ProcessResult result, bool cancelled)
    {
        if (cancelled) return ("Cancelled", "Cancelled by user.");
        if (result.TimedOut) return ("Failed", "Timed out.");

        // WinGet return codes: https://github.com/microsoft/winget-cli/blob/master/doc/windows/package-manager/winget/returnCodes.md
        switch (unchecked((uint)result.ExitCode))
        {
            case 0:
                return LooksAlreadyInstalled(result.Output)
                    ? ("AlreadyInstalled", "Already installed.")
                    : ("Installed", "Installed successfully.");
            case 3010: // ERROR_SUCCESS_REBOOT_REQUIRED from an installer passed straight through
            case 1641: // ERROR_SUCCESS_REBOOT_INITIATED
            case 0x8A150109: // INSTALL_REBOOT_REQUIRED_TO_FINISH
            case 0x8A15010B: // INSTALL_REBOOT_INITIATED
                return ("RebootRequired", "Installed. Restart the PC to finish.");
            case 0x8A15002B: // UPDATE_NOT_APPLICABLE
            case 0x8A150061: // PACKAGE_ALREADY_INSTALLED
                return ("AlreadyInstalled", "Already installed.");
            case 0x8A15010D: // INSTALL_ALREADY_INSTALLED
                return ("AlreadyInstalled", "Another version is already installed.");
            case 0x8A15010E: // INSTALL_DOWNGRADE
                return ("AlreadyInstalled", "A newer version is already installed.");
            case 0x8A150014: // NO_APPLICATIONS_FOUND
                return ("Failed", "No matching WinGet package.");
            case 0x8A150010: // NO_APPLICABLE_INSTALLER
                return ("Failed", "No installer for this system's architecture or OS.");
            case 0x8A150011: // INSTALLER_HASH_MISMATCH
                return ("Failed", "Installer hash mismatch.");
            case 0x8A150019: // COMMAND_REQUIRES_ADMIN
                return ("Failed", "Needs elevation. Re-run TeaChest as administrator.");
            case 0x8A150056: // INSTALLER_PROHIBITS_ELEVATION
                return ("Failed", "This installer refuses to run as administrator. Re-run TeaChest without elevation.");
            case 0x8A150101: // INSTALL_PACKAGE_IN_USE
            case 0x8A150103: // INSTALL_FILE_IN_USE
            case 0x8A150111: // INSTALL_PACKAGE_IN_USE_BY_APPLICATION
                return ("Failed", "The app or its files are in use. Close it and retry.");
            case 0x8A150102: // INSTALL_INSTALL_IN_PROGRESS
                return ("Failed", "Another installation is in progress. Retry when it finishes.");
            case 0x8A150104: // INSTALL_MISSING_DEPENDENCY
            case 0x8A150110: // INSTALL_DEPENDENCIES
                return ("Failed", "A dependency could not be installed.");
            case 0x8A150105: // INSTALL_DISK_FULL
                return ("Failed", "Not enough disk space.");
            case 0x8A150107: // INSTALL_NO_NETWORK
                return ("Failed", "The installer needs an internet connection.");
            case 0x8A15010A: // INSTALL_REBOOT_REQUIRED_FOR_INSTALL
                return ("Failed", "Restart the PC, then retry this app.");
            case 0x8A15010C: // INSTALL_CANCELLED_BY_USER
                return ("Failed", "The installer was cancelled.");
            case 0x8A15010F: // INSTALL_BLOCKED_BY_POLICY
                return ("Failed", "Blocked by organization policy.");
            case 0x8A150041: // PACKAGE_AGREEMENTS_NOT_ACCEPTED
                return ("Failed", "Package agreements were not accepted.");
        }

        // Unknown code: fall back to the (English-only) output text, then the last line.
        if (LooksAlreadyInstalled(result.Output)) return ("AlreadyInstalled", "Already installed.");
        if (result.Output.Contains("requires administrator", StringComparison.OrdinalIgnoreCase) ||
            result.Output.Contains("access is denied", StringComparison.OrdinalIgnoreCase))
        {
            return ("Failed", "Needs elevation. Re-run TeaChest as administrator.");
        }
        return ("Failed", LastMeaningfulLine(result.Output, "WinGet returned an error."));
    }

    private static bool WriteManualActions(InstallPlan plan, InstallReport report, string manualPath)
    {
        List<PlanItem> manualItems = plan.Items.Where(i => i.Action == "Manual").ToList();
        List<InstallResultRecord> failed = report.Results.Where(r => r.Status == "Failed").ToList();
        List<InstallResultRecord> reboot = report.Results.Where(r => r.Status == "RebootRequired").ToList();
        if (manualItems.Count == 0 && failed.Count == 0 && reboot.Count == 0) return false;

        var manual = new StringBuilder();
        manual.AppendLine("Manual actions from TeaChest");
        manual.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        manual.AppendLine();
        if (reboot.Count > 0)
        {
            manual.AppendLine($"Restart this PC to finish installing: {string.Join(", ", reboot.Select(r => r.Name))}");
            manual.AppendLine();
        }
        foreach (PlanItem item in manualItems)
        {
            manual.AppendLine($"- {item.Name} [{item.Type}]");
            manual.AppendLine($"  {item.Detail}");
            if (string.Equals(item.Type, "msstore", StringComparison.OrdinalIgnoreCase) && item.Id.Length == 12)
            {
                manual.AppendLine($"  ms-windows-store://pdp/?ProductId={item.Id}");
            }
            manual.AppendLine();
        }
        foreach (InstallResultRecord record in failed)
        {
            manual.AppendLine($"- {record.Name} [{record.Type}]");
            manual.AppendLine($"  {record.Detail}");
            manual.AppendLine();
        }
        File.WriteAllText(manualPath, manual.ToString(), new UTF8Encoding(false));
        return true;
    }

    private static InstallReport BuildReport(List<InstallResultRecord> results, InstallPlan plan, string logPath, string reportPath, string manualPath)
    {
        return new InstallReport
        {
            Installed = results.Count(r => r.Status == "Installed"),
            RebootRequired = results.Count(r => r.Status == "RebootRequired"),
            AlreadyInstalled = results.Count(r => r.Status == "AlreadyInstalled"),
            Manual = plan.ManualCount,
            Failed = results.Count(r => r.Status == "Failed"),
            Results = results,
            LogPath = logPath,
            ReportPath = reportPath,
            ManualActionsPath = manualPath
        };
    }

    private static bool LooksAlreadyInstalled(string output)
        => output.Contains("No newer package versions are available", StringComparison.OrdinalIgnoreCase)
           || output.Contains("No applicable update", StringComparison.OrdinalIgnoreCase)
           || output.Contains("already installed", StringComparison.OrdinalIgnoreCase);

    private static string ExitCode(int code)
        => code >= 0 ? $"0x{code:X8}" : $"0x{(uint)code:X8}";

    private static string LastMeaningfulLine(string output, string fallback)
    {
        string? line = output.Replace("\r", "")
            .Split('\n')
            .Select(s => s.Trim())
            .LastOrDefault(s => s.Length > 0);
        return string.IsNullOrWhiteSpace(line) ? fallback : TextUtil.Shorten(line, 300);
    }

    private static string Tail(string value, int max)
        => value.Length <= max ? value : value[^max..];

    private sealed class RunnerOnlyBackend : IInstallBackend
    {
        private readonly Func<AppItem, InstallOptions, Action<string>?, CancellationToken, Task<ProcessResult>> _runInstall;

        public RunnerOnlyBackend(Func<AppItem, InstallOptions, Action<string>?, CancellationToken, Task<ProcessResult>> runInstall)
            => _runInstall = runInstall;

        public Task<InstalledIndex> BuildInstalledIndexAsync(CancellationToken cancellationToken)
            => Task.FromResult(new InstalledIndex());

        public Task<ProcessResult> InstallAsync(AppItem app, InstallOptions options, Action<string>? onLine, CancellationToken cancellationToken)
            => _runInstall(app, options, onLine, cancellationToken);
    }
}
