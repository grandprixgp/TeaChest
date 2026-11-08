using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>Builds the installed-app index from what Windows knows: the registry, AppX, and WinGet.</summary>
public static class WindowsInstalledIndex
{
    public static async Task<InstalledIndex> BuildAsync(CancellationToken cancellationToken)
    {
        var registry = new RegistryInventoryService();
        var appx = new AppxInventoryService();
        var winget = new WingetService();

        Task<IReadOnlyList<AppItem>> registryTask = registry.ScanAsync(cancellationToken);
        Task<IReadOnlyList<AppItem>> appxTask = appx.ScanAsync(cancellationToken);
        Task<WingetInventory> wingetTask = winget.GetInventoryAsync(cancellationToken);

        await Task.WhenAll(registryTask, appxTask, wingetTask).ConfigureAwait(false);
        return Build(registryTask.Result, appxTask.Result, wingetTask.Result);
    }

    internal static InstalledIndex Build(IEnumerable<AppItem> registry, IEnumerable<AppItem> appx, WingetInventory winget)
    {
        var index = new InstalledIndex
        {
            HasWingetInventory = winget.Warning == null && winget.Rows.Count > 0
        };

        foreach (AppItem item in registry)
        {
            index.AddName(item.Name);
        }

        foreach (AppItem item in appx)
        {
            index.AddName(item.Name);
            if (item.PackageFamilyName.Length > 0) index.PackageFamilies.Add(item.PackageFamilyName);
            if (item.PackageFullName.Length > 0) index.Ids.Add(item.PackageFullName);
        }

        foreach (WingetRow row in winget.Rows)
        {
            index.AddName(row.Name);
            if (!string.IsNullOrWhiteSpace(row.Id)) index.Ids.Add(row.Id);
        }

        foreach (ExportedPackage package in winget.Exported)
        {
            index.Ids.Add(package.Id);
        }

        return index;
    }
}
