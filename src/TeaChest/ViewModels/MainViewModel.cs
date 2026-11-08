using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Input;
using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.ViewModels;

public enum AppFilter
{
    All,
    Selected,
    NotSelected,
    Manual,
    Store,
    Winget,
    WorthChecking
}

// A class rather than a record so a tab can change its label and hide itself without being
// swapped out, which would drop the ListBox selection.
public sealed class FilterOption : INotifyPropertyChanged
{
    private string _label;
    private bool _isVisible = true;

    public FilterOption(string label, AppFilter filter)
    {
        _label = label;
        Filter = filter;
    }

    public AppFilter Filter { get; }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
    }

    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsVisible)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => Label;
}

public sealed class AppErrorEventArgs : EventArgs
{
    public AppErrorEventArgs(string title, string message)
    {
        Title = title;
        Message = message;
    }

    public string Title { get; }
    public string Message { get; }
}

/// <summary>One labelled line in the details panel.</summary>
public sealed record DetailRow(string Label, string Value, bool IsMono = false);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private const string ManifestFilter = "JSON manifest (*.json)|*.json|All files (*.*)|*.*";

    private readonly ManifestService _manifestService = new();
    private readonly InstallService _installService = new(new WingetInstallBackend());
    private readonly IDialogService _dialogs;
    private CancellationTokenSource? _cts;
    private InstallPlan? _lastPlan;
    private string _searchText = "";
    private string _statusText = "Ready";
    private string _logText = "";
    private string _manifestPath = "";
    private string _planSummary = "Preview a plan to see what will be installed.";
    private FilterOption _selectedFilter;
    private bool _isBusy;
    private bool _isScanning;
    private bool _isProgressIndeterminate;
    private double _progressValue;
    private bool _forceReinstall;
    private bool _allowInteractive;
    private bool _skipStore;
    private bool _matchScope;
    private bool _pinVersions;
    private int _totalCount;
    private int _selectedCount;
    private int _wingetCount;
    private int _manualCount;
    private int _storeCount;
    private int _worthCheckingCount;
    private readonly FilterOption _worthCheckingFilter = new("Worth a check", AppFilter.WorthChecking) { IsVisible = false };
    private InstallReport? _lastReport;
    private string _origin = "";
    private AppItem? _selectedApp;
    private bool _isDetailsOpen;
    private IReadOnlyList<DetailRow> _selectedDetails = Array.Empty<DetailRow>();
    private bool _hasStoreLink;
    private bool _hasWingetLink;
    private bool _hasInstallFolder;

    public MainViewModel(IDialogService? dialogs = null)
    {
        // Unowned real dialogs if none is given, so older callers that never open one needn't pass it.
        _dialogs = dialogs ?? new WpfDialogService(null);
        Filters = new ObservableCollection<FilterOption>
        {
            new("All apps", AppFilter.All),
            new("Selected", AppFilter.Selected),
            new("Not selected", AppFilter.NotSelected),
            new("Manual", AppFilter.Manual),
            new("Store", AppFilter.Store),
            new("WinGet", AppFilter.Winget),
            _worthCheckingFilter
        };
        _selectedFilter = Filters[0];

        Apps = new ObservableCollection<AppItem>();
        AppsView = CollectionViewSource.GetDefaultView(Apps);
        AppsView.Filter = FilterApp;
        PlanItems = new ObservableCollection<PlanItem>();

        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectVisibleCommand = new RelayCommand(() => SetVisibleSelection(true), () => !IsBusy && TotalCount > 0);
        ClearVisibleCommand = new RelayCommand(() => SetVisibleSelection(false), () => !IsBusy && TotalCount > 0);
        RestoreDefaultsCommand = new RelayCommand(RestoreDefaults, () => !IsBusy && TotalCount > 0);
        CloseDetailsCommand = new RelayCommand(() => IsDetailsOpen = false);
        // Each link is rebuilt from the row at click time, never from a string kept since selection.
        OpenStoreCommand = new RelayCommand(() => OpenLink(AppLinks.StoreLink(SelectedApp)));
        OpenWingetCommand = new RelayCommand(() => OpenLink(AppLinks.WingetLink(SelectedApp)));
        OpenFolderCommand = new RelayCommand(() => OpenLink(AppLinks.LocalFolder(SelectedApp)));
        // These were async void click handlers. A RelayCommand with an async lambda keeps them exactly
        // that: AsyncRelayCommand would grey each button while its flow runs, even behind its own dialogs.
        OpenCommand = new RelayCommand(async () => await OpenManifestAsync());
        LoadCommand = new RelayCommand(async () => await LoadManifestFromPickerAsync());
        SaveCommand = new RelayCommand(async () => await SaveManifestFromPickerAsync());
        PreviewCommand = new RelayCommand(async () => await PreviewAsync());
        InstallCommand = new RelayCommand(async () => await InstallWithConfirmAsync());
        RetryCommand = new RelayCommand(async () => await RetryWithConfirmAsync());

        DefaultManifestPath = Path.Combine(AppContext.BaseDirectory, ManifestService.DefaultFileName);
        VersionText = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    }

    public ObservableCollection<AppItem> Apps { get; }
    public ICollectionView AppsView { get; }
    public ObservableCollection<PlanItem> PlanItems { get; }
    public ObservableCollection<FilterOption> Filters { get; }

    public AsyncRelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SelectVisibleCommand { get; }
    public RelayCommand ClearVisibleCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand CloseDetailsCommand { get; }
    public RelayCommand OpenStoreCommand { get; }
    public RelayCommand OpenWingetCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenCommand { get; }
    public RelayCommand LoadCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand RetryCommand { get; }

    public string DefaultManifestPath { get; }
    public string VersionText { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            OnPropertyChanged();
            AppsView.Refresh();
        }
    }

    public FilterOption SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value == null || _selectedFilter == value) return;
            _selectedFilter = value;
            OnPropertyChanged();
            AppsView.Refresh();
        }
    }

    public string StatusText
    {
        get => _statusText;
        set => SetField(ref _statusText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetField(ref _logText, value);
    }

    public string ManifestPath
    {
        get => _manifestPath;
        private set
        {
            // Any scan or file load replaces apps that came from another device.
            _origin = "";
            SetField(ref _manifestPath, value);
            OnPropertyChanged(nameof(ManifestLabel));
        }
    }

    public string ManifestLabel => !string.IsNullOrWhiteSpace(ManifestPath)
        ? ManifestPath
        : _origin.Length > 0
            ? $"From {_origin} (not saved)"
            : "Using the current selection (not saved)";

    public string PlanSummary
    {
        get => _planSummary;
        private set => SetField(ref _planSummary, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanInstall));
            NotifyRetryChanged();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value)) OnPropertyChanged(nameof(CanEdit));
        }
    }

    public bool CanEdit => !IsBusy;
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetField(ref _isProgressIndeterminate, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetField(ref _progressValue, value);
    }

    public bool ForceReinstall
    {
        get => _forceReinstall;
        set { if (SetField(ref _forceReinstall, value)) InvalidatePlan("Install options changed. Preview the plan again."); }
    }

    public bool AllowInteractive
    {
        get => _allowInteractive;
        set { if (SetField(ref _allowInteractive, value)) InvalidatePlan("Install options changed. Preview the plan again."); }
    }

    public bool SkipStore
    {
        get => _skipStore;
        set { if (SetField(ref _skipStore, value)) InvalidatePlan("Install options changed. Preview the plan again."); }
    }

    public bool MatchScope
    {
        get => _matchScope;
        set { if (SetField(ref _matchScope, value)) InvalidatePlan("Install options changed. Preview the plan again."); }
    }

    public bool PinVersions
    {
        get => _pinVersions;
        set { if (SetField(ref _pinVersions, value)) InvalidatePlan("Install options changed. Preview the plan again."); }
    }

    public int TotalCount
    {
        get => _totalCount;
        private set
        {
            if (SetField(ref _totalCount, value)) OnPropertyChanged(nameof(HasApps));
        }
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetField(ref _selectedCount, value);
    }

    public int WingetCount
    {
        get => _wingetCount;
        private set => SetField(ref _wingetCount, value);
    }

    public int ManualCount
    {
        get => _manualCount;
        private set => SetField(ref _manualCount, value);
    }

    public int StoreCount
    {
        get => _storeCount;
        private set => SetField(ref _storeCount, value);
    }

    public int WorthCheckingCount
    {
        get => _worthCheckingCount;
        private set
        {
            if (!SetField(ref _worthCheckingCount, value)) return;
            _worthCheckingFilter.Label = WorthCheckingLabel(value);
            _worthCheckingFilter.IsVisible = value > 0;
            // Nothing left to check, so don't leave the grid empty behind a hidden tab.
            if (value == 0 && SelectedFilter == _worthCheckingFilter) SelectedFilter = Filters[0];
        }
    }

    /// <summary>The row the details panel shows. Null when nothing is picked in the grid.</summary>
    public AppItem? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (!SetField(ref _selectedApp, value)) return;
            // Worked out once per pick: the folder check touches the disk.
            SelectedDetails = value == null ? Array.Empty<DetailRow>() : DetailRows(value);
            HasStoreLink = AppLinks.StoreLink(value) != null;
            HasWingetLink = AppLinks.WingetLink(value) != null;
            HasInstallFolder = AppLinks.LocalFolder(value) != null;
            OnPropertyChanged(nameof(HasSelectedApp));
        }
    }

    public bool HasSelectedApp => SelectedApp != null;

    public bool IsDetailsOpen
    {
        get => _isDetailsOpen;
        set => SetField(ref _isDetailsOpen, value);
    }

    public IReadOnlyList<DetailRow> SelectedDetails
    {
        get => _selectedDetails;
        private set => SetField(ref _selectedDetails, value);
    }

    public bool HasStoreLink
    {
        get => _hasStoreLink;
        private set => SetField(ref _hasStoreLink, value);
    }

    public bool HasWingetLink
    {
        get => _hasWingetLink;
        private set => SetField(ref _hasWingetLink, value);
    }

    public bool HasInstallFolder
    {
        get => _hasInstallFolder;
        private set => SetField(ref _hasInstallFolder, value);
    }

    public bool HasApps => TotalCount > 0;
    public bool HasPlan => PlanItems.Count > 0;
    public bool CanInstall => !IsBusy && _lastPlan != null && _lastPlan.InstallCount > 0;
    public int InstallActionCount => PlanItems.Count(i => i.Action == "Install");
    public InstallReport? LastReport
    {
        get => _lastReport;
        private set => SetField(ref _lastReport, value);
    }

    /// <summary>The failed rows in the current plan, ready for a retry. Empty before a run.</summary>
    public IReadOnlyList<PlanItem> FailedItems => SelectFailed(PlanItems);

    public int RetryCount => FailedItems.Count;

    /// <summary>Drives the Retry failed button: nothing to retry, or a run in progress, hides it.</summary>
    public bool CanRetry => !IsBusy && RetryCount > 0;

    public string RetryLabel => $"Retry {RetryCount} failed";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<AppErrorEventArgs>? Error;

    /// <summary>Raised after Open, so the window can switch to the Collect view.</summary>
    public event EventHandler? CollectViewRequested;

    public async Task ScanAsync()
    {
        if (IsBusy) return;
        List<AppItem> previous = Apps.Select(a => a.Clone()).ToList();
        string previousManifest = ManifestPath;
        _cts = new CancellationTokenSource();
        IsBusy = true;
        IsScanning = true;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        StatusText = "Scanning registry, AppX, and WinGet in parallel...";
        _lastPlan = null;
        PlanItems.Clear();
        NotifyPlanChanged();

        try
        {
            var service = new ScanService();
            var progress = new Progress<ScanSnapshot>(ApplySnapshot);
            ScanResult result = await service.ScanAsync(progress, _cts.Token);
            ApplySnapshot(new ScanSnapshot
            {
                Apps = result.Apps,
                Status = $"Scan complete · {result.Apps.Count} apps · {result.Apps.Count(a => a.IsSelected)} selected",
                IsComplete = true,
                Progress = 1,
                IsIndeterminate = false
            });

            foreach (string warning in result.Warnings)
            {
                AppendLog("WARN: " + warning);
            }
            StatusText = $"Scan complete in {result.Duration.TotalSeconds:0.0}s · {SelectedCount} selected";
            ManifestPath = "";
        }
        catch (OperationCanceledException)
        {
            ReplaceApps(previous);
            ManifestPath = previousManifest;
            StatusText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            ReplaceApps(previous);
            ManifestPath = previousManifest;
            StatusText = "Scan failed.";
            Error?.Invoke(this, new AppErrorEventArgs("Scan failed", ex.Message));
        }
        finally
        {
            IsBusy = false;
            IsScanning = false;
            IsProgressIndeterminate = false;
            ProgressValue = 0;
            _cts?.Dispose();
            _cts = null;
            RefreshCounts();
        }
    }

    public async Task LoadManifestAsync(string path)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            ManifestDocument document = await _manifestService.LoadAsync(path);
            List<AppItem> items = ManifestService.ToItems(document);
            foreach (AppItem item in items) AppClassifier.ApplyClassification(item);
            ReplaceApps(items);
            ManifestPath = Path.GetFullPath(path);
            _lastPlan = null;
            PlanItems.Clear();
            NotifyPlanChanged();
            PlanSummary = "Preview a plan to see what will be installed.";
            StatusText = $"Loaded {items.Count} apps · {SelectedCount} selected";
            AppendLog($"Loaded manifest: {ManifestPath}");
            foreach (string warning in document.LoadWarnings) AppendLog("WARN: " + warning);
            if (document.LoadWarnings.Count > 0) StatusText += $" · {document.LoadWarnings.Count} warning(s), see log";
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Could not load manifest", ex.Message));
        }
        finally
        {
            IsBusy = false;
            RefreshCounts();
        }
    }

    /// <summary>Replaces the app list with items received from another device.</summary>
    public bool LoadItems(IEnumerable<AppItem> items, string origin)
    {
        if (IsBusy) return false;
        List<AppItem> list = items.ToList();
        foreach (AppItem item in list) AppClassifier.ApplyClassification(item);
        ReplaceApps(list);
        ManifestPath = "";
        _origin = origin;
        OnPropertyChanged(nameof(ManifestLabel));
        _lastPlan = null;
        PlanItems.Clear();
        NotifyPlanChanged();
        PlanSummary = "Preview a plan to see what will be installed.";
        StatusText = $"Loaded {list.Count} apps from {origin} · {SelectedCount} selected";
        AppendLog($"Loaded {list.Count} apps from {origin}.");
        return true;
    }

    /// <summary>The current app list as a manifest, for sharing with another device.</summary>
    public ManifestDocument CreateShareDocument() => ManifestService.CreateDocument(Apps);

    public void Log(string line) => AppendLog(line);

    public async Task SaveManifestAsync(string path)
    {
        if (IsBusy) return;
        if (Apps.Count == 0)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Nothing to save", "Scan this PC or load a manifest first."));
            return;
        }

        IsBusy = true;
        try
        {
            await _manifestService.SaveAsync(path, Apps);
            ManifestPath = Path.GetFullPath(path);
            StatusText = $"Saved {Apps.Count} apps · {SelectedCount} selected";
            AppendLog($"Saved manifest: {ManifestPath}");
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Could not save manifest", ex.Message));
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task<InstallPlan?> BuildPlanAsync()
    {
        if (IsBusy) return null;
        if (SelectedCount == 0)
        {
            Error?.Invoke(this, new AppErrorEventArgs("No apps selected", "Select at least one app before previewing or installing."));
            return null;
        }

        _cts = new CancellationTokenSource();
        IsBusy = true;
        _lastPlan = null;
        PlanItems.Clear();
        NotifyPlanChanged();
        StatusText = "Building install plan...";
        ProgressValue = 0;
        IsProgressIndeterminate = true;

        try
        {
            var options = CurrentInstallOptions();
            InstallPlan plan = await _installService.BuildPlanAsync(Apps, options, _cts.Token);
            _lastPlan = plan;
            foreach (PlanItem item in plan.Items) PlanItems.Add(item);
            NotifyPlanChanged();
            PlanSummary = $"{plan.InstallCount} to install · {plan.SkipCount} already present · {plan.ManualCount} manual";
            ProgressValue = 100;
            StatusText = plan.Warning ?? "Plan ready. Review it before installing.";
            if (!string.IsNullOrWhiteSpace(plan.Warning)) AppendLog("WARN: " + plan.Warning);
            AppendLog($"Plan: {plan.InstallCount} to install, {plan.SkipCount} already present, {plan.ManualCount} manual.");
            return plan;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Plan cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Plan failed.";
            Error?.Invoke(this, new AppErrorEventArgs("Plan failed", ex.Message));
            return null;
        }
        finally
        {
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public async Task<InstallReport?> InstallAsync()
    {
        if (IsBusy) return null;
        if (_lastPlan == null || _lastPlan.InstallCount == 0)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Nothing to install", "Preview the plan first; there are no automatic installs."));
            return null;
        }

        string logDirectory = string.IsNullOrWhiteSpace(ManifestPath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(ManifestPath) ?? AppContext.BaseDirectory;

        _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        AppendLog("");
        AppendLog("Starting installation...");
        StatusText = "Installing applications...";

        try
        {
            // Progress<T> captures the UI context here, so the row updates happen on the UI thread.
            var progress = new Progress<InstallProgress>(p =>
            {
                ApplyInstallProgress(p);
                ProgressValue = p.Total > 0 ? (double)p.Current / p.Total * 100 : 0;
                if (!string.IsNullOrWhiteSpace(p.Status))
                {
                    StatusText = p.Status == "Installing"
                        ? $"Installing {p.AppName} ({p.Current}/{p.Total})"
                        : $"{p.Status}: {p.AppName}";
                }
                if (!string.IsNullOrWhiteSpace(p.Line)) AppendLog(p.Line);
            });

            InstallReport report = await _installService.InstallAsync(
                _lastPlan, CurrentInstallOptions(), logDirectory, progress, _cts.Token);
            LastReport = report;
            _lastPlan = null;
            OnPropertyChanged(nameof(CanInstall));
            ProgressValue = 100;
            string reboot = report.RebootRequired > 0 ? $" · {report.RebootRequired} need a restart" : "";
            StatusText = report.Cancelled
                ? $"Install cancelled · {report.Installed + report.RebootRequired} installed · {report.NotAttempted} not attempted"
                : $"Install finished · {report.Installed} installed{reboot} · {report.Failed} failed · {report.Manual} manual";
            AppendLog($"Report: {report.ReportPath}");
            if (!string.IsNullOrWhiteSpace(report.ManualActionsPath)) AppendLog($"Manual actions: {report.ManualActionsPath}");
            return report;
        }
        catch (OperationCanceledException)
        {
            StatusText = "Install cancelled.";
            return null;
        }
        catch (Exception ex)
        {
            StatusText = "Install failed.";
            Error?.Invoke(this, new AppErrorEventArgs("Install failed", ex.Message));
            return null;
        }
        finally
        {
            // Statuses changed outside NotifyPlanChanged, so the retry list needs its own nudge.
            NotifyRetryChanged();
            IsBusy = false;
            IsProgressIndeterminate = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Runs the rows that failed last time again, as their own plan, so a retry gets its own log and
    /// report. The rows are reused in place, which is what lets successes drop off the retry list.
    /// </summary>
    public async Task<InstallReport?> RetryFailedAsync()
    {
        if (IsBusy) return null;
        InstallPlan plan = BuildRetryPlan(PlanItems);
        if (plan.InstallCount == 0)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Nothing to retry", "No rows failed in the last run."));
            return null;
        }

        _lastPlan = plan;
        NotifyRetryChanged();
        return await InstallAsync();
    }

    // ---- Button flows ---------------------------------------------------------------------

    public async Task OpenManifestAsync()
    {
        if (PickManifestFile() is { } path)
        {
            await LoadManifestAsync(path);
            CollectViewRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    public async Task LoadManifestFromPickerAsync()
    {
        if (PickManifestFile() is { } path)
        {
            await LoadManifestAsync(path);
        }
    }

    public async Task SaveManifestFromPickerAsync()
    {
        string initial = ManifestPath.Length > 0 ? ManifestPath : DefaultManifestPath;
        string? path = _dialogs.PickSaveFile(
            "Save TeaChest manifest",
            ManifestFilter,
            "json",
            Path.GetFileName(initial),
            Path.GetDirectoryName(initial) ?? AppContext.BaseDirectory);
        if (path != null)
        {
            await SaveManifestAsync(path);
        }
    }

    public async Task PreviewAsync()
    {
        if (IsBusy) return;
        await BuildPlanAsync();
    }

    public async Task InstallWithConfirmAsync()
    {
        if (IsBusy) return;
        await RunInstallFlowAsync(_dialogs, BuildPlanAsync, InstallAsync);
    }

    public async Task RetryWithConfirmAsync()
    {
        if (IsBusy) return;
        await RunRetryFlowAsync(_dialogs, RetryCount, RetryFailedAsync);
    }

    public void ShowPlanItem(PlanItem item)
        => _dialogs.Show($"{item.Name}\n\n{item.Detail}\n\n{item.Id}", "Plan item", DialogIcon.Information);

    private string? PickManifestFile() => _dialogs.PickOpenFile("Open TeaChest manifest", ManifestFilter);

    /// <summary>Plan, then ask, then install, then show the report. The plan and install steps are passed in so tests can fake them.</summary>
    internal static async Task RunInstallFlowAsync(
        IDialogService dialogs,
        Func<Task<InstallPlan?>> buildPlan,
        Func<Task<InstallReport?>> install)
    {
        InstallPlan? plan = await buildPlan();
        if (plan == null) return;
        if (plan.InstallCount == 0)
        {
            dialogs.Show(
                "There is nothing to install automatically. Check the plan for manual entries or skipped apps.",
                "Nothing to install",
                DialogIcon.Information);
            return;
        }

        if (!ConfirmInstall(dialogs, plan.InstallCount, plan.SkipCount, plan.ManualCount)) return;

        InstallReport? report = await install();
        if (report == null) return;
        ShowInstallReport(dialogs, report);
    }

    internal static async Task RunRetryFlowAsync(IDialogService dialogs, int count, Func<Task<InstallReport?>> retry)
    {
        if (count == 0) return;

        // Confirm before the retry plan resets the rows, so declining changes nothing.
        if (!ConfirmInstall(dialogs, count, 0, 0)) return;

        InstallReport? report = await retry();
        if (report == null) return;
        ShowInstallReport(dialogs, report);
    }

    private static bool ConfirmInstall(IDialogService dialogs, int installCount, int skipCount, int manualCount)
    {
        string others = skipCount + manualCount > 0
            ? $"The plan also contains {skipCount} already-present and {manualCount} manual item(s).\n\n"
            : "";
        return dialogs.Confirm(
            $"Install {installCount} app(s) now?\n\n" +
            others +
            "This will change this PC.",
            "Confirm install",
            ConfirmButtons.YesNo,
            DialogIcon.Warning);
    }

    private static void ShowInstallReport(IDialogService dialogs, InstallReport report)
    {
        string message = $"Installed: {report.Installed}\nAlready present: {report.AlreadyInstalled}\nManual: {report.Manual}\nFailed: {report.Failed}";
        if (report.RebootRequired > 0) message += $"\nInstalled, restart needed: {report.RebootRequired}";
        if (report.Cancelled) message += $"\nNot attempted (cancelled): {report.NotAttempted}";
        message += $"\n\nReport: {report.ReportPath}\nLog: {report.LogPath}";
        if (report.ManualActionsPath.Length > 0) message += $"\nManual actions: {report.ManualActionsPath}";
        bool warn = report.Failed > 0 || report.Cancelled;
        string title = report.Cancelled ? "Install cancelled"
            : report.Failed > 0 ? "Install finished with errors"
            : report.RebootRequired > 0 ? "Install complete - restart needed"
            : "Install complete";
        dialogs.Show(message, title, warn ? DialogIcon.Warning : DialogIcon.Information);
    }

    // The row carries its own plan item, so this needs no lookup and no guessing from the name.
    internal static void ApplyInstallProgress(InstallProgress progress)
    {
        if (progress.Item == null || string.IsNullOrEmpty(progress.Status)) return;
        progress.Item.Status = progress.Status;
        progress.Item.StatusDetail = progress.Detail;
    }

    internal static IReadOnlyList<PlanItem> SelectFailed(IEnumerable<PlanItem> items)
        => items.Where(item => item.Status == "Failed").ToList();

    /// <summary>
    /// A plan of just the failed rows, always marked Install. The rows are the same objects, not
    /// copies, so the retry updates the grid in place and the failed list reflects the new outcome.
    /// </summary>
    internal static InstallPlan BuildRetryPlan(IEnumerable<PlanItem> items)
    {
        var plan = new InstallPlan();
        foreach (PlanItem item in SelectFailed(items))
        {
            item.Action = "Install";
            item.Status = "";
            item.StatusDetail = "";
            plan.Items.Add(item);
        }
        return plan;
    }

    public void Cancel()
    {
        if (_cts == null) return;
        StatusText = "Cancelling...";
        _cts.Cancel();
    }

    public void SelectVisible() => SetVisibleSelection(true);
    public void ClearVisible() => SetVisibleSelection(false);

    public void RestoreDefaults()
    {
        foreach (AppItem app in Apps) app.IsSelected = app.DefaultSelected;
        RefreshCounts();
        StatusText = "Restored automatic defaults.";
    }

    private void SetVisibleSelection(bool selected)
    {
        foreach (object item in AppsView)
        {
            if (item is AppItem app) app.IsSelected = selected;
        }
        RefreshCounts();
    }

    private void InvalidatePlan(string reason)
    {
        if (_lastPlan == null && PlanItems.Count == 0) return;
        _lastPlan = null;
        PlanItems.Clear();
        NotifyPlanChanged();
        PlanSummary = reason;
    }

    private InstallOptions CurrentInstallOptions() => new()
    {
        Force = ForceReinstall,
        Interactive = AllowInteractive,
        SkipStore = SkipStore,
        MatchScope = MatchScope,
        PinVersions = PinVersions
    };

    private void ApplySnapshot(ScanSnapshot snapshot)
    {
        CollectionDiff.Apply(
            Apps,
            snapshot.Apps,
            AppKey,
            HasSameContent,
            app => app.PropertyChanged += App_PropertyChanged,
            app => app.PropertyChanged -= App_PropertyChanged);
        StatusText = snapshot.Status;
        IsProgressIndeterminate = snapshot.IsIndeterminate;
        ProgressValue = snapshot.IsIndeterminate ? 8 : snapshot.Progress * 100;
        if (snapshot.IsComplete) IsScanning = false;
        RefreshCounts();
    }

    // The name is the stable key: a package ID can appear or change when a lookup resolves mid-scan.
    internal static string AppKey(AppItem app)
    {
        string key = TextUtil.NormalizeStrict(app.Name);
        return key.Length > 0 ? key : app.IdentityKey;
    }

    // Every writable field is compared: a later source can fill in only a hidden one, and keeping
    // the old instance would then drop that data from the grid, the saved manifest, and matching.
    internal static bool HasSameContent(AppItem current, AppItem next)
        => string.Equals(current.Name, next.Name, StringComparison.Ordinal)
           && string.Equals(current.Id, next.Id, StringComparison.Ordinal)
           && string.Equals(current.Source, next.Source, StringComparison.Ordinal)
           && string.Equals(current.InstallType, next.InstallType, StringComparison.Ordinal)
           && current.MatchConfidence == next.MatchConfidence
           && string.Equals(current.Version, next.Version, StringComparison.Ordinal)
           && string.Equals(current.Publisher, next.Publisher, StringComparison.Ordinal)
           && string.Equals(current.Scope, next.Scope, StringComparison.Ordinal)
           && string.Equals(current.PackageFamilyName, next.PackageFamilyName, StringComparison.Ordinal)
           && string.Equals(current.PackageFullName, next.PackageFullName, StringComparison.Ordinal)
           && string.Equals(current.Category, next.Category, StringComparison.Ordinal)
           && string.Equals(current.Notes, next.Notes, StringComparison.Ordinal)
           && string.Equals(current.UninstallString, next.UninstallString, StringComparison.Ordinal)
           && string.Equals(current.InstallLocation, next.InstallLocation, StringComparison.Ordinal)
           && string.Equals(current.StoreSearchName, next.StoreSearchName, StringComparison.Ordinal)
           && string.Equals(current.DownloadUrl, next.DownloadUrl, StringComparison.Ordinal)
           && string.Equals(current.Sha256, next.Sha256, StringComparison.Ordinal)
           && string.Equals(current.InstallerType, next.InstallerType, StringComparison.Ordinal)
           && string.Equals(current.InstallerArgs, next.InstallerArgs, StringComparison.Ordinal)
           && string.Equals(current.WingetArgs, next.WingetArgs, StringComparison.Ordinal)
           && current.DefaultSelected == next.DefaultSelected
           && current.IsSelected == next.IsSelected;

    private void ReplaceApps(IEnumerable<AppItem> items)
    {
        foreach (AppItem app in Apps) app.PropertyChanged -= App_PropertyChanged;
        Apps.Clear();
        foreach (AppItem app in items)
        {
            app.PropertyChanged += App_PropertyChanged;
            Apps.Add(app);
        }
        AppsView.Refresh();
        RefreshCounts();
    }

    private void App_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItem.IsSelected))
        {
            _lastPlan = null;
            if (PlanItems.Count > 0)
            {
                PlanItems.Clear();
                NotifyPlanChanged();
                PlanSummary = "Selection changed. Preview the plan again.";
            }
            RefreshCounts();
        }
    }

    private void RefreshCounts()
    {
        TotalCount = Apps.Count;
        SelectedCount = Apps.Count(a => a.IsSelected);
        WingetCount = Apps.Count(a => a.IsWinget);
        ManualCount = Apps.Count(a => a.IsManual);
        StoreCount = Apps.Count(a => a.IsStore);
        WorthCheckingCount = Apps.Count(a => a.IsLooseMatch);
        CommandManager.InvalidateRequerySuggested();
    }

    internal static string WorthCheckingLabel(int count)
        => count > 0 ? $"Worth a check ({count})" : "Worth a check";

    private bool FilterApp(object item) => item is AppItem app && Matches(app, SelectedFilter.Filter, SearchText);

    internal static bool Matches(AppItem app, AppFilter filter, string search)
    {
        switch (filter)
        {
            case AppFilter.Selected when !app.IsSelected:
            case AppFilter.NotSelected when app.IsSelected:
            case AppFilter.Manual when !app.IsManual:
            case AppFilter.Store when !app.IsStore:
            case AppFilter.Winget when !app.IsWinget:
            case AppFilter.WorthChecking when !app.IsLooseMatch:
                return false;
        }

        string trimmed = search.Trim();
        return trimmed.Length == 0 || app.SearchBlob.Contains(trimmed, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The panel's lines for one row, skipping empty fields but always saying how it was matched.</summary>
    internal static IReadOnlyList<DetailRow> DetailRows(AppItem app)
    {
        var rows = new List<DetailRow>();
        void Add(string label, string? value, bool mono = false)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new DetailRow(label, value.Trim(), mono));
        }

        Add("ID", app.Id, mono: true);
        Add("Type", app.TypeLabel);
        Add("Version", app.Version);
        Add("Category", app.Category);
        Add("Publisher", app.Publisher);
        Add("Scope", app.Scope);
        Add("Source", app.Source);
        Add("How it was matched", DescribeMatch(app));
        Add("Notes", app.Notes);
        return rows;
    }

    internal static string DescribeMatch(AppItem app)
    {
        string where = SourceLabel(app.Source);
        return app.MatchConfidence switch
        {
            MatchConfidence.Loose => $"A loose name match against {where}. Worth a quick check.",
            MatchConfidence.Exact => $"An exact name match against {where}.",
            _ when !string.IsNullOrWhiteSpace(app.Id) => $"The ID came straight from {where}, with no name guess involved.",
            _ => $"Found in {where}, with no package ID."
        };
    }

    private static string SourceLabel(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "winget" => "WinGet",
        "msstore" => "the Microsoft Store",
        "msix" => "the installed Store packages",
        "registry" => "the registry",
        "manifest" => "a manifest",
        null or "" => "an unknown source",
        _ => source.Trim()
    };

    private void OpenLink(string? target)
    {
        if (target == null) return;
        try
        {
            AppLinks.Open(target);
        }
        catch (Exception ex)
        {
            Error?.Invoke(this, new AppErrorEventArgs("Could not open link", ex.Message));
        }
    }

    private void NotifyPlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(InstallActionCount));
        NotifyRetryChanged();
        OnPropertyChanged(nameof(CanInstall));
        CommandManager.InvalidateRequerySuggested();
    }

    private void NotifyRetryChanged()
    {
        OnPropertyChanged(nameof(FailedItems));
        OnPropertyChanged(nameof(RetryCount));
        OnPropertyChanged(nameof(CanRetry));
        OnPropertyChanged(nameof(RetryLabel));
    }

    private void AppendLog(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        string next = _logText + "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine;
        if (next.Length > 400_000) next = next[^250_000..];
        LogText = next;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
