using System.IO;

namespace TeaChest.Services;

/// <summary>Where this app keeps per-user data, and the one-time move from the AppMigrate days.</summary>
public static class AppPaths
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string DataDirectory { get; } = Path.Combine(LocalAppData, "TeaChest");

    public static void MoveLegacyData() => MoveLegacyData(Path.Combine(LocalAppData, "AppMigrate"), DataDirectory);

    /// <summary>
    /// Moves files from the old folder so this PC keeps its identity and its pairings. The DPAPI
    /// protection is tied to the Windows user, not the path, so the device key still opens.
    /// Anything already at the destination wins. A failure only means pairing again.
    /// </summary>
    internal static void MoveLegacyData(string legacyDirectory, string dataDirectory)
    {
        if (!Directory.Exists(legacyDirectory)) return;
        try
        {
            Directory.CreateDirectory(dataDirectory);
            foreach (string file in Directory.GetFiles(legacyDirectory))
            {
                string target = Path.Combine(dataDirectory, Path.GetFileName(file));
                if (!File.Exists(target)) File.Move(file, target);
            }
            if (!Directory.EnumerateFileSystemEntries(legacyDirectory).Any()) Directory.Delete(legacyDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
