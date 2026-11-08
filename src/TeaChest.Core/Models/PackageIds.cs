using System.Text.RegularExpressions;

namespace TeaChest.Models;

public static class PackageIds
{
    // Mirrors the WinGet manifest schema for PackageIdentifier: 2-8 dot-separated segments,
    // each 1-32 chars, excluding path, wildcard, and quote characters. A leading '-' is also
    // rejected so an ID can never be read as a command-line flag.
    private static readonly Regex WingetId = new(
        @"^(?!-)[^.\s\\/:*?""<>|\x00-\x1f]{1,32}(\.[^.\s\\/:*?""<>|\x00-\x1f]{1,32}){1,7}$",
        RegexOptions.Compiled);

    private static readonly Regex StoreId = new(@"^[0-9A-Z]{12}$", RegexOptions.Compiled);

    public static bool IsWingetId(string? id)
        => !string.IsNullOrEmpty(id) && WingetId.IsMatch(id);

    public static bool IsStoreId(string? id)
        => !string.IsNullOrEmpty(id) && StoreId.IsMatch(id.ToUpperInvariant());
}
