using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>
/// The package manager side of installing: what this PC already has, and installing one app.
/// <see cref="InstallService"/> keeps the plan, the order, the log and the report, which are
/// the same on every OS.
/// </summary>
public interface IInstallBackend
{
    /// <summary>What's installed here, so the plan can skip it. May throw; the plan then carries a warning instead.</summary>
    Task<InstalledIndex> BuildInstalledIndexAsync(CancellationToken cancellationToken);

    /// <summary>Installs one app, handing each line of output to <paramref name="onLine"/> as it arrives.</summary>
    Task<ProcessResult> InstallAsync(AppItem app, InstallOptions options, Action<string>? onLine, CancellationToken cancellationToken);
}
