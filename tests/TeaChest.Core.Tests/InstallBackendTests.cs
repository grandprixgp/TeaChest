using TeaChest.Models;
using TeaChest.Services;

namespace TeaChest.Tests;

public sealed class InstallBackendTests
{
    private sealed class FakeBackend : IInstallBackend
    {
        public InstalledIndex Index { get; init; } = new();
        public Exception? IndexFailure { get; init; }

        public Task<InstalledIndex> BuildInstalledIndexAsync(CancellationToken cancellationToken)
            => IndexFailure == null ? Task.FromResult(Index) : Task.FromException<InstalledIndex>(IndexFailure);

        public Task<ProcessResult> InstallAsync(AppItem app, InstallOptions options, Action<string>? onLine, CancellationToken cancellationToken)
            => Task.FromResult(new ProcessResult(0, "", false));
    }

    private static AppItem Winget(string name, string id)
        => new() { Name = name, Id = id, Source = "winget", InstallType = "winget", IsSelected = true };

    [Fact]
    public async Task PlanSkipsWhatTheBackendSaysIsInstalled()
    {
        var index = new InstalledIndex { HasWingetInventory = true };
        index.Ids.Add("Git.Git");
        var service = new InstallService(new FakeBackend { Index = index });

        InstallPlan plan = await service.BuildPlanAsync(
            new[] { Winget("Git", "Git.Git"), Winget("7-Zip", "7zip.7zip") }, new InstallOptions(), CancellationToken.None);

        Assert.Equal(new[] { "Skip", "Install" }, plan.Items.Select(item => item.Action));
        Assert.Null(plan.Warning);
    }

    [Fact]
    public async Task IndexThatFailsStillGivesAPlanWithAWarning()
    {
        var service = new InstallService(new FakeBackend { IndexFailure = new InvalidOperationException("winget broke") });

        InstallPlan plan = await service.BuildPlanAsync(new[] { Winget("Git", "Git.Git") }, new InstallOptions(), CancellationToken.None);

        Assert.Equal("Install", Assert.Single(plan.Items).Action);
        Assert.Contains("winget broke", plan.Warning);
    }
}
