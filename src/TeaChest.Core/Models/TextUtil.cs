using System.Text.RegularExpressions;

namespace TeaChest.Models;

public static class TextUtil
{
    private static readonly Regex Parenthetical = new(@"\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex TrailingVersion = new(@"\s+\d[\w.\-]*\s*$", RegexOptions.Compiled);
    private static readonly Regex VersionToken = new(@"\b(v?ersion\s*)?v?\d+(\.\d+){1,4}\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Architecture = new(@"\b(x64|x86|64-bit|32-bit|user|machine)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Keep letters and digits from every script, so non-Latin names still have a key.
    // Marks stay too: in Thai and Hindi they're the vowels, so dropping them merges different words.
    // CleanAscii stays ASCII-only because that one is for package IDs.
    private static readonly Regex NonAlphaNumeric = new(@"[^\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    public static string NormalizeLoose(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string text = value.ToLowerInvariant();
        text = Parenthetical.Replace(text, " ");
        text = TrailingVersion.Replace(text, " ");
        text = VersionToken.Replace(text, " ");
        text = Architecture.Replace(text, " ");
        text = NonAlphaNumeric.Replace(text, "");
        if (text.StartsWith("microsoft", StringComparison.Ordinal)) text = text[9..];
        if (text == "msteams") text = "teams";
        return text;
    }

    public static string NormalizeStrict(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        string text = value.ToLowerInvariant();
        text = Parenthetical.Replace(text, " ");
        text = Architecture.Replace(text, " ");
        text = NonAlphaNumeric.Replace(text, "");
        if (text.StartsWith("microsoft", StringComparison.Ordinal)) text = text[9..];
        if (text == "msteams") text = "teams";
        return text;
    }

    public static string CleanAscii(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return new string(value.Where(c => c is >= ' ' and <= '~').ToArray()).Trim();
    }

    public static bool LooksTruncated(string? value)
        => !string.IsNullOrEmpty(value) && value.Any(c => c > '~' || c == '\u2026' || c == '\uFFFD');

    public static string Shorten(string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        value = value.Trim();
        return value.Length <= max ? value : value[..(max - 1)] + "…";
    }
}
