using System.IO;
using System.Net;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.Services.Lan;

namespace TeaChest.Tests;

// The Nearby tests that need Windows: network profiles and the WinGet-built index. The rest
// of Nearby is tested in TeaChest.Core.Tests.

public class NetworkProfileTests
{
    private static readonly Guid Ethernet = Guid.NewGuid(), Warp = Guid.NewGuid(), Sandbox = Guid.NewGuid();

    // Mirrors a real desktop: Ethernet LAN, Cloudflare WARP tunnel, Windows Sandbox NAT switch.
    private static readonly Dictionary<Guid, AdapterInfo> Adapters = new()
    {
        [Ethernet] = new AdapterInfo(Ethernet, "Ethernet", HasIPv4Gateway: true, HasLanAddress: true),
        [Warp] = new AdapterInfo(Warp, "CloudflareWARP", HasIPv4Gateway: false, HasLanAddress: false),
        [Sandbox] = new AdapterInfo(Sandbox, "vEthernet (AppSandboxNAT)", HasIPv4Gateway: false, HasLanAddress: true)
    };

    [Fact]
    public void PublicLanIsFlagged()
    {
        var networks = NetworkProfiles.SelectLanNetworks(new[]
        {
            new NetworkConnectionInfo("Network", 0, Ethernet),
            new NetworkConnectionInfo("CloudflareWARP", 0, Warp)
        }, Adapters);

        LanNetwork lan = Assert.Single(networks);
        Assert.Equal(("Network", "Ethernet", true), (lan.Name, lan.Adapter, lan.IsPublic));
    }

    [Fact]
    public void PublicVpnAloneIsNotAWarning()
    {
        var networks = NetworkProfiles.SelectLanNetworks(new[]
        {
            new NetworkConnectionInfo("Network", 1, Ethernet),
            new NetworkConnectionInfo("CloudflareWARP", 0, Warp)
        }, Adapters);

        Assert.DoesNotContain(networks, n => n.IsPublic);
    }

    [Fact]
    public void UnknownAndVirtualAdaptersAreIgnored()
    {
        var networks = NetworkProfiles.SelectLanNetworks(new[]
        {
            new NetworkConnectionInfo("Sandbox", 0, Sandbox),
            new NetworkConnectionInfo("Mystery", 0, Guid.NewGuid())
        }, Adapters);

        Assert.Empty(networks);
    }

    [Fact]
    public void RealMachineQueryDoesNotThrow() => NetworkProfiles.GetLanNetworks();
}

public class InventoryDiffTests
{
    [Fact]
    public void SplitsIntoMissingAlreadyAndOnlyHere()
    {
        var index = WindowsInstalledIndex.Build(
            Array.Empty<AppItem>(),
            Array.Empty<AppItem>(),
            new WingetInventory(new[] { new WingetRow("Git", "Git.Git", "2", "winget", false, false, false, false) }, null));
        var remote = new[]
        {
            new AppItem { Name = "Git", Id = "Git.Git", Source = "winget", InstallType = "winget", IsSelected = true },
            new AppItem { Name = "7-Zip", Id = "7zip.7zip", Source = "winget", InstallType = "winget", IsSelected = true }
        };
        var local = new[]
        {
            new AppItem { Name = "Git", Id = "Git.Git" },
            new AppItem { Name = "Paint.NET", Id = "dotPDN.PaintDotNet" }
        };

        InventoryDiffResult diff = InventoryDiff.Compare(remote, index, local);

        Assert.Equal("7zip.7zip", Assert.Single(diff.MissingHere).Id);
        AppItem already = Assert.Single(diff.AlreadyHere);
        Assert.False(already.IsSelected);
        Assert.Equal("dotPDN.PaintDotNet", Assert.Single(diff.OnlyHere).Id);
    }
}
