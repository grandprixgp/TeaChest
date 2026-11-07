using System.Diagnostics;
using System.IO;
using TeaChest.Models;

namespace TeaChest.Services;

/// <summary>
/// Builds the links in the details panel. Rows can come from someone else's manifest, so nothing
/// in an <see cref="AppItem"/> is ever opened as it stands: a web or Store link only comes from one
/// of two fixed templates filled with an ID that passed <see cref="PackageIds"/>, and a folder only
/// from a plain local path that exists.
/// </summary>
internal static class AppLinks
{
    private const string StoreTemplate = "ms-windows-store://pdp/?ProductId=";
    private const string WingetRunTemplate = "https://winget.run/pkg/";

    internal static string? StoreLink(string? id)
        => PackageIds.IsStoreId(id) ? StoreTemplate + id!.ToUpperInvariant() : null;

    internal static string? WingetLink(string? id)
    {
        if (!PackageIds.IsWingetId(id)) return null;
        int dot = id!.IndexOf('.');
        string publisher = id[..dot];
        string name = id[(dot + 1)..];
        return WingetRunTemplate + Uri.EscapeDataString(publisher) + "/" + Uri.EscapeDataString(name);
    }

    internal static string? StoreLink(AppItem? app) => app == null ? null : StoreLink(app.Id);

    // Only for rows WinGet installs: an extra source can carry a dotted ID that isn't a package.
    internal static string? WingetLink(AppItem? app) => app is { IsWinget: true } ? WingetLink(app.Id) : null;

    /// <summary>
    /// The install folder, normalised and ending in a separator, or null. Only a drive-letter path
    /// is allowed: for a UNC or device path, even the existence check would reach out to another
    /// machine, and the location may have come from someone else's manifest.
    /// </summary>
    internal static string? LocalFolder(string? location, Func<string, bool> directoryExists)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        string path = location.Trim().Trim('"').Trim();
        if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/')) return null;
        // A second colon would be an alternate data stream, not a folder.
        if (path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;

        try
        {
            path = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
        if (!directoryExists(path)) return null;

        // The trailing separator stops the shell from trying "C:\Foo" as "C:\Foo.exe" and friends.
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    internal static string? LocalFolder(AppItem? app) => app == null ? null : LocalFolder(app.InstallLocation, Directory.Exists);

    /// <summary>Hands a link built above to the shell. Throws if Windows can't open it.</summary>
    internal static void Open(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
}
