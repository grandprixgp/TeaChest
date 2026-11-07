using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Threading;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest.Tests;

/// <summary>Stands in for the real dialogs: records what was asked and gives canned answers.</summary>
internal sealed class FakeDialogs : IDialogService
{
    public bool ConfirmAnswer { get; set; } = true;
    public string? OpenAnswer { get; set; }
    public string? SaveAnswer { get; set; }

    public List<(string Message, string Title, ConfirmButtons Buttons, DialogIcon Icon)> Confirms { get; } = new();
    public List<(string Message, string Title, DialogIcon Icon)> Shown { get; } = new();
    public List<(string Title, string Filter)> OpenPicks { get; } = new();
    public List<(string Title, string Filter, string Extension, string FileName, string Directory)> SavePicks { get; } = new();

    public string? PickOpenFile(string title, string filter)
    {
        OpenPicks.Add((title, filter));
        return OpenAnswer;
    }

    public string? PickSaveFile(string title, string filter, string defaultExtension, string fileName, string initialDirectory)
    {
        SavePicks.Add((title, filter, defaultExtension, fileName, initialDirectory));
        return SaveAnswer;
    }

    public bool Confirm(string message, string title, ConfirmButtons buttons, DialogIcon icon)
    {
        Confirms.Add((message, title, buttons, icon));
        return ConfirmAnswer;
    }

    public void Show(string message, string title, DialogIcon icon) => Shown.Add((message, title, icon));
}

public sealed class InstallFlowTests : IDisposable
{
    private readonly DirectoryInfo _temp = Directory.CreateTempSubdirectory("teachest-tests-");

    public void Dispose() => _temp.Delete(recursive: true);

    private static InstallPlan Plan(int install, int skip = 0, int manual = 0)
    {
        var plan = new InstallPlan();
        for (int i = 0; i < install; i++) plan.Items.Add(new PlanItem { Action = "Install" });
        for (int i = 0; i < skip; i++) plan.Items.Add(new PlanItem { Action = "Skip" });
        for (int i = 0; i < manual; i++) plan.Items.Add(new PlanItem { Action = "Manual" });
        return plan;
    }

    private static InstallReport Report() => new()
    {
        Installed = 2,
        AlreadyInstalled = 1,
        Manual = 0,
        Failed = 0,
        ReportPath = @"C:\r.json",
        LogPath = @"C:\l.txt"
    };

    [Fact]
    public async Task InstallFlowWithConfirmYesInstallsAndShowsTheReport()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = true };
        var steps = new List<string>();

        await MainViewModel.RunInstallFlowAsync(
            dialogs,
            () => { steps.Add("plan"); return Task.FromResult<InstallPlan?>(Plan(2, skip: 1)); },
            () => { steps.Add("install"); return Task.FromResult<InstallReport?>(Report()); });

        Assert.Equal(new[] { "plan", "install" }, steps);
        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Equal("Install 2 app(s) now?\n\nThe plan also contains 1 already-present and 0 manual item(s).\n\nThis will change this PC.", confirm.Message);
        Assert.Equal("Confirm install", confirm.Title);
        Assert.Equal(ConfirmButtons.YesNo, confirm.Buttons);
        Assert.Equal(DialogIcon.Warning, confirm.Icon);

        var report = Assert.Single(dialogs.Shown);
        Assert.Equal("Install complete", report.Title);
        Assert.Equal("Installed: 2\nAlready present: 1\nManual: 0\nFailed: 0\n\nReport: C:\\r.json\nLog: C:\\l.txt", report.Message);
        Assert.Equal(DialogIcon.Information, report.Icon);
    }

    [Fact]
    public async Task InstallFlowWithConfirmNoInstallsNothing()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        bool installed = false;

        await MainViewModel.RunInstallFlowAsync(
            dialogs,
            () => Task.FromResult<InstallPlan?>(Plan(3)),
            () => { installed = true; return Task.FromResult<InstallReport?>(Report()); });

        Assert.False(installed);
        Assert.Equal("Install 3 app(s) now?\n\nThis will change this PC.", Assert.Single(dialogs.Confirms).Message);
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task InstallFlowWithNothingToInstallSaysSoWithoutAsking()
    {
        var dialogs = new FakeDialogs();
        bool installed = false;

        await MainViewModel.RunInstallFlowAsync(
            dialogs,
            () => Task.FromResult<InstallPlan?>(Plan(0, skip: 2, manual: 1)),
            () => { installed = true; return Task.FromResult<InstallReport?>(Report()); });

        Assert.False(installed);
        Assert.Empty(dialogs.Confirms);
        var shown = Assert.Single(dialogs.Shown);
        Assert.Equal("There is nothing to install automatically. Check the plan for manual entries or skipped apps.", shown.Message);
        Assert.Equal("Nothing to install", shown.Title);
        Assert.Equal(DialogIcon.Information, shown.Icon);
    }

    [Fact]
    public async Task InstallFlowStopsQuietlyWhenNoPlanWasBuilt()
    {
        var dialogs = new FakeDialogs();

        await MainViewModel.RunInstallFlowAsync(
            dialogs,
            () => Task.FromResult<InstallPlan?>(null),
            () => throw new InvalidOperationException("Should not install without a plan"));

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public async Task CancelledInstallShowsAWarningReport()
    {
        var dialogs = new FakeDialogs();
        var report = new InstallReport { Installed = 1, Cancelled = true, NotAttempted = 4, ReportPath = "r", LogPath = "l", ManualActionsPath = "m" };

        await MainViewModel.RunInstallFlowAsync(
            dialogs,
            () => Task.FromResult<InstallPlan?>(Plan(5)),
            () => Task.FromResult<InstallReport?>(report));

        var shown = Assert.Single(dialogs.Shown);
        Assert.Equal("Install cancelled", shown.Title);
        Assert.Equal("Installed: 1\nAlready present: 0\nManual: 0\nFailed: 0\nNot attempted (cancelled): 4\n\nReport: r\nLog: l\nManual actions: m", shown.Message);
        Assert.Equal(DialogIcon.Warning, shown.Icon);
    }

    [Fact]
    public async Task DecliningARetryLeavesTheRowsAlone()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        bool retried = false;

        await MainViewModel.RunRetryFlowAsync(dialogs, 2, () => { retried = true; return Task.FromResult<InstallReport?>(Report()); });

        Assert.False(retried);
        Assert.Equal("Install 2 app(s) now?\n\nThis will change this PC.", Assert.Single(dialogs.Confirms).Message);
    }

    [Fact]
    public async Task RetryWithNothingFailedAsksNothing()
    {
        var dialogs = new FakeDialogs();

        await MainViewModel.RunRetryFlowAsync(dialogs, 0, () => throw new InvalidOperationException("Nothing to retry"));

        Assert.Empty(dialogs.Confirms);
        Assert.Empty(dialogs.Shown);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void UseMissingAppsAsksBeforeReplacingAList(bool answer, bool expectUsed)
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = answer };
        bool used = false;

        bool result = NearbyViewModel.UseMissingFlow(dialogs, isBusy: false, hasApps: true, () => used = true);

        Assert.Equal(expectUsed, used);
        Assert.Equal(expectUsed, result);
        var confirm = Assert.Single(dialogs.Confirms);
        Assert.Equal("This replaces the current app list with the apps missing on this PC. Save a manifest first if you need the current list.\n\nContinue?", confirm.Message);
        Assert.Equal("Use missing apps", confirm.Title);
        Assert.Equal(ConfirmButtons.OkCancel, confirm.Buttons);
        Assert.Equal(DialogIcon.Question, confirm.Icon);
    }

    [Fact]
    public void UseMissingAppsWithAnEmptyListSkipsTheConfirm()
    {
        var dialogs = new FakeDialogs { ConfirmAnswer = false };
        bool used = false;

        Assert.True(NearbyViewModel.UseMissingFlow(dialogs, isBusy: false, hasApps: false, () => used = true));

        Assert.True(used);
        Assert.Empty(dialogs.Confirms);
        Assert.Empty(dialogs.Shown);
    }

    [Fact]
    public void UseMissingAppsWaitsForABusyApp()
    {
        var dialogs = new FakeDialogs();
        bool used = false;

        Assert.False(NearbyViewModel.UseMissingFlow(dialogs, isBusy: true, hasApps: true, () => used = true));

        Assert.False(used);
        Assert.Empty(dialogs.Confirms);
        var shown = Assert.Single(dialogs.Shown);
        Assert.Equal("Wait for the current operation to finish first.", shown.Message);
        Assert.Equal("TeaChest", shown.Title);
        Assert.Equal(DialogIcon.Information, shown.Icon);
    }

    [Fact]
    public async Task CancellingSaveWritesNothing()
    {
        var dialogs = new FakeDialogs { SaveAnswer = null };
        var viewModel = new MainViewModel(dialogs);
        Assert.True(viewModel.LoadItems(new[] { new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget" } }, "another PC"));
        string status = viewModel.StatusText;
        var errors = new List<AppErrorEventArgs>();
        viewModel.Error += (_, e) => errors.Add(e);

        await viewModel.SaveManifestFromPickerAsync();

        var pick = Assert.Single(dialogs.SavePicks);
        Assert.Equal("Save TeaChest manifest", pick.Title);
        Assert.Equal("JSON manifest (*.json)|*.json|All files (*.*)|*.*", pick.Filter);
        Assert.Equal("json", pick.Extension);
        Assert.Equal(Path.GetFileName(viewModel.DefaultManifestPath), pick.FileName);
        Assert.Equal(Path.GetDirectoryName(viewModel.DefaultManifestPath), pick.Directory);
        Assert.Equal("", viewModel.ManifestPath);
        Assert.Equal(status, viewModel.StatusText);
        Assert.Empty(errors);
    }

    [Fact]
    public async Task SaveOffersTheCurrentManifestAndWritesWhereThePickerSays()
    {
        string first = Path.Combine(_temp.FullName, "first.json");
        string second = Path.Combine(_temp.FullName, "second.json");
        var dialogs = new FakeDialogs { SaveAnswer = first };
        var viewModel = new MainViewModel(dialogs);
        viewModel.LoadItems(new[] { new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget" } }, "another PC");

        await viewModel.SaveManifestFromPickerAsync();
        dialogs.SaveAnswer = second;
        await viewModel.SaveManifestFromPickerAsync();

        Assert.True(File.Exists(first));
        Assert.True(File.Exists(second));
        Assert.Equal(("first.json", _temp.FullName), (dialogs.SavePicks[1].FileName, dialogs.SavePicks[1].Directory));
        Assert.Equal(Path.GetFullPath(second), viewModel.ManifestPath);
    }

    // The manifest service resumes off the caller's thread, so a real load needs the dispatcher the
    // window would give it, or the app list's view refuses the change.
    private static void RunOnDispatcher(Func<Task> body)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.InvokeAsync(async () =>
            {
                try { await body(); }
                catch (Exception ex) { error = ex; }
                finally { frame.Continue = false; }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) ExceptionDispatchInfo.Throw(error);
    }

    [Fact]
    public async Task OpenLoadsThenAsksForTheCollectView()
    {
        string path = Path.Combine(_temp.FullName, "apps.json");
        await new ManifestService().SaveAsync(path, new[] { new AppItem { Name = "Git", Id = "Git.Git", InstallType = "winget" } });
        var dialogs = new FakeDialogs { OpenAnswer = path };
        int appsWhenAsked = -1;
        var errors = new List<string>();
        string manifestPath = "";

        RunOnDispatcher(async () =>
        {
            var viewModel = new MainViewModel(dialogs);
            viewModel.CollectViewRequested += (_, _) => appsWhenAsked = viewModel.TotalCount;
            viewModel.Error += (_, e) => errors.Add(e.Title + ": " + e.Message);
            await viewModel.OpenManifestAsync();
            manifestPath = viewModel.ManifestPath;
        });

        Assert.Empty(errors);
        Assert.Equal(("Open TeaChest manifest", "JSON manifest (*.json)|*.json|All files (*.*)|*.*"), Assert.Single(dialogs.OpenPicks));
        Assert.Equal(1, appsWhenAsked);
        Assert.Equal(Path.GetFullPath(path), manifestPath);
    }

    [Fact]
    public async Task CancellingOpenOrLoadChangesNothing()
    {
        var dialogs = new FakeDialogs { OpenAnswer = null };
        var viewModel = new MainViewModel(dialogs);
        bool asked = false;
        viewModel.CollectViewRequested += (_, _) => asked = true;

        await viewModel.OpenManifestAsync();
        await viewModel.LoadManifestFromPickerAsync();

        Assert.Equal(2, dialogs.OpenPicks.Count);
        Assert.False(asked);
        Assert.Equal(0, viewModel.TotalCount);
    }

    [Fact]
    public void PlanItemDetailsKeepTheirText()
    {
        var dialogs = new FakeDialogs();
        var viewModel = new MainViewModel(dialogs);

        viewModel.ShowPlanItem(new PlanItem { Name = "Git", Detail = "WinGet package.", Id = "Git.Git" });

        Assert.Equal(("Git\n\nWinGet package.\n\nGit.Git", "Plan item", DialogIcon.Information), Assert.Single(dialogs.Shown));
    }
}
