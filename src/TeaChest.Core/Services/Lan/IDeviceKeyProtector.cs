namespace TeaChest.Services.Lan;

/// <summary>
/// Keeps this PC's private key unreadable to anyone but the current user. Each OS does it its
/// own way; on Windows it's DPAPI.
/// </summary>
public interface IDeviceKeyProtector
{
    /// <summary>
    /// The key file's name in the data folder. Each kind of protection has its own, so one never
    /// tries to open a file another one wrote.
    /// </summary>
    string FileName { get; }

    byte[] Protect(byte[] data);

    /// <summary>Throws a CryptographicException when the data can't be opened, such as a file copied from another user.</summary>
    byte[] Unprotect(byte[] data);
}
