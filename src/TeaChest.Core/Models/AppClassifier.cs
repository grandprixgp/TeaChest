using System.Text.RegularExpressions;

namespace TeaChest.Models;

public sealed class SelectionOptions
{
    public bool IncludeAll { get; set; }
    public bool NoStore { get; set; }
    public bool NoModule { get; set; }
    public List<string> Excludes { get; set; } = new();
}

public static class AppClassifier
{
    private static readonly Regex RuntimePattern = new(
        @"Visual C\+\+|ASP\.NET|(?<!\w)\.NET|\.NET(?=\.)|App Runtime|WinAppRuntime|WindowsAppRuntime|VCLibs|UI\.Xaml|Native (Framework|Runtime)|WebView2|DirectX|Windows SDK|Winget Source|Windows Package Manager|Windows Subsystem for Linux|\bWSL\b|App Installer",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DriverPattern = new(
        @"\bDriver\b|Virtual Display|Virtual USB|NVIDIA Graphics|AMD Chipset|Realtek|Intel\(R\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InboxWinget = new(
        @"^(Microsoft Edge|Microsoft OneDrive|OneDrive|Microsoft Teams|Outlook for Windows|Microsoft 365 Copilot|Windows Terminal|App Installer|Windows App Runtime.*|Microsoft Visual C\+\+.*|Microsoft \.NET.*|Microsoft\.UI\.Xaml.*|Microsoft\.VCLibs.*|Windows Package Manager.*|WinGet.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex InboxAppx = new(
        @"^(windows(store|calculator|camera|notepad|soundrecorder|alarms|feedbackhub|terminal|photos|devhome|clientwebexperience|crossdevice)|paint|screensketch|storepurchaseapp|gethelp|bing(news|search|weather)|gamingapp|xbox|yourphone|zunemusic|todos|powerautomatedesktop|rawimageextension|heifimageextension|hevcvideoextension|av1videoextension|avcencodervideoextension|mpeg2videoextension|vp9videoextensions|webmediaextensions|webpimageextension|microsoftedge|microsoftofficehub|microsoftsolitairecollection|microsoftstickynotes|outlookforwindows|sechealthui|desktopappinstaller|wingetsource|applicationcompatibilityenhancements|windowsappruntime|uixaml|vclibs|netnative|clipchamp|teams|onedrivesync|startexperiencesapp|widgetsplatformruntime|corporationiiquickassist)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> InboxNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Clipchamp", "Microsoft Clipchamp", "Solitaire & Casual Games", "Sticky Notes", "Microsoft Sticky Notes",
        "Paint", "Snipping Tool", "Xbox", "Game Bar", "Phone Link", "Quick Assist", "Microsoft To Do",
        "Power Automate", "Windows Media Player", "Windows Web Experience Pack", "Mobile devices",
        "Start Experiences App", "Store Experience Host", "Windows Application Compatibility Enhancements",
        "Widgets Platform Runtime", "Game Speech Window", "Xbox TCUI", "Windows Security"
    };

    /// <summary>
    /// Sources whose rows are add-ons rather than apps, by lower-cased IExtraSource name. A dev PC
    /// can have 200 VS Code extensions, so these start unticked. A new source adds its name here.
    /// </summary>
    public static readonly IReadOnlySet<string> AddOnSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "vs code", "scoop", "chocolatey", "npm", "pipx", "dotnet tool", "wsl"
    };

    public static bool IsAddOn(AppItem item) => AddOnSources.Contains(item.Source);

    public static string GetCategory(string name, string installType)
    {
        if (RuntimePattern.IsMatch(name)) return "Runtime";
        if (DriverPattern.IsMatch(name)) return "Driver";
        if (Regex.IsMatch(name, "Chrome|Firefox|Microsoft Edge|Brave|\\bOpera\\b|Vivaldi|Arc Browser|\\bego\\b", RegexOptions.IgnoreCase)) return "Browser";
        if (Regex.IsMatch(name, "Parsec|RustDesk|TeamViewer|AnyDesk|Remote Desktop|VNC", RegexOptions.IgnoreCase)) return "Remote access";
        if (Regex.IsMatch(name, "Antigravity|Command Code|OpenCode|Visual Studio|VS Code|GitHub|TortoiseGit|\\bGit|Python|Node\\.js|Docker|Postman|Terminal|PowerShell", RegexOptions.IgnoreCase)) return "Development";
        if (Regex.IsMatch(name, "ChatGPT|Claude|Gemini|Copilot|Codex|Ollama|LM Studio", RegexOptions.IgnoreCase)) return "AI tools";
        if (Regex.IsMatch(name, "Teams|Slack|Discord|Zoom|WhatsApp|Telegram|Signal|Outlook|Thunderbird|Mail", RegexOptions.IgnoreCase)) return "Communication";
        if (Regex.IsMatch(name, "Spotify|VLC|iTunes|Media Player|\\bOBS\\b|Audacity|Clipchamp|Photos|Image|Video|Music", RegexOptions.IgnoreCase)) return "Media";
        if (Regex.IsMatch(name, "Steam\\b|SteamVR|Epic Games|GOG|Xbox|Battle\\.net|Game", RegexOptions.IgnoreCase)) return "Gaming";
        if (Regex.IsMatch(name, "Office|\\bWord\\b|\\bExcel\\b|PowerPoint|Notion|Obsidian|Todo|Sticky|PDF|Acrobat|OneNote", RegexOptions.IgnoreCase)) return "Productivity";
        if (Regex.IsMatch(name, "Security|Defender|1Password|Bitwarden|Malware|VPN|Password", RegexOptions.IgnoreCase)) return "Security";
        if (string.Equals(installType, "msix", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(installType, "msstore", StringComparison.OrdinalIgnoreCase)) return "Store app";
        return "Utilities";
    }

    public static bool IsInbox(AppItem item)
    {
        if (item.PackageFamilyName.Length > 0)
        {
            string identity = item.PackageFamilyName.Split('_')[0];
            string normalized = TextUtil.NormalizeLoose(identity);
            if (InboxAppx.IsMatch(normalized)) return true;
        }

        string name = item.Name.Trim();
        if (InboxNames.Contains(name)) return true;
        if (InboxWinget.IsMatch(name)) return true;
        return false;
    }

    public static bool IsStoreItem(AppItem item)
        => item.IsStore
           || string.Equals(item.Source, "msix", StringComparison.OrdinalIgnoreCase)
           || string.Equals(item.Source, "msstore", StringComparison.OrdinalIgnoreCase);

    public static void ApplyClassification(AppItem item)
    {
        item.InstallType = NormalizeInstallType(item);
        item.Category = GetCategory(item.Name, item.InstallType);
        bool hasActionableId = !string.IsNullOrWhiteSpace(item.Id) &&
                               (item.IsWinget || string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase));
        if (hasActionableId &&
            (item.Notes.StartsWith("No WinGet package found", StringComparison.OrdinalIgnoreCase) ||
             item.Notes.StartsWith("Store/MSIX app", StringComparison.OrdinalIgnoreCase) ||
             item.Notes.StartsWith("WinGet reported", StringComparison.OrdinalIgnoreCase)))
        {
            item.Notes = "";
        }
        if (item.IsManual && string.IsNullOrWhiteSpace(item.Notes))
        {
            item.Notes = item.IsStore
                ? "Store/MSIX app. Reinstall it from the Microsoft Store."
                : "No WinGet package found. Reinstall manually.";
        }
        if (string.IsNullOrWhiteSpace(item.StoreSearchName)) item.StoreSearchName = item.Name;
    }

    public static void ApplyDefaults(AppItem item, SelectionOptions options)
    {
        ApplyClassification(item);
        bool runtime = item.Category == "Runtime";
        bool driver = item.Category == "Driver";
        bool inbox = IsInbox(item);
        bool include = options.IncludeAll || !(runtime || driver || inbox);
        if (options.NoStore && IsStoreItem(item)) include = false;
        if (IsAddOn(item)) include = false;
        foreach (string pattern in options.Excludes)
        {
            if (Matches(item.Name, pattern) || Matches(item.Id, pattern))
            {
                include = false;
                break;
            }
        }
        item.DefaultSelected = include;
        item.IsSelected = include;
    }

    public static string NormalizeInstallType(AppItem item)
    {
        if (string.Equals(item.InstallType, "winget", StringComparison.OrdinalIgnoreCase)) return "winget";
        if (string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)) return "msstore";
        if (string.Equals(item.InstallType, "download", StringComparison.OrdinalIgnoreCase)) return "download";
        if (string.Equals(item.Source, "winget", StringComparison.OrdinalIgnoreCase) &&
            (!string.IsNullOrWhiteSpace(item.Id) || string.Equals(item.InstallType, "winget", StringComparison.OrdinalIgnoreCase))) return "winget";
        if (string.Equals(item.Source, "msstore", StringComparison.OrdinalIgnoreCase)) return "msstore";
        return "manual";
    }

    private static bool Matches(string value, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, regex, RegexOptions.IgnoreCase);
        }
        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }
}
