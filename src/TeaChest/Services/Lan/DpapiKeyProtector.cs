using System.Security.Cryptography;

namespace TeaChest.Services.Lan;

/// <summary>Encrypts the device key for the current Windows user with DPAPI.</summary>
public sealed class DpapiKeyProtector : IDeviceKeyProtector
{
    // The name AppMigrate used too, so existing pairings keep working.
    public string FileName => "device.pfx.dpapi";

    public byte[] Protect(byte[] data) => ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] data) => ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}
