using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>Installs through WinGet, and checks what's already here with the registry, AppX and WinGet.</summary>
public sealed class WingetInstallBackend : IInstallBackend
{
    private readonly WingetService _winget = new();

    public Task<InstalledIndex> BuildInstalledIndexAsync(CancellationToken cancellationToken)
        => WindowsInstalledIndex.BuildAsync(cancellationToken);

    public Task<ProcessResult> InstallAsync(AppItem app, InstallOptions options, Action<string>? onLine, CancellationToken cancellationToken)
        => _winget.InstallAsync(app, options.Force, options.Interactive, options.MatchScope, options.PinVersions, cancellationToken, onLine);
}
