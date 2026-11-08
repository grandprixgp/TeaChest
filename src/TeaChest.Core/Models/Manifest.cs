using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace TeaChest.Models;

public sealed class ManifestDocument
{
    public int SchemaVersion { get; set; } = 1;
    public string GeneratedAt { get; set; } = DateTimeOffset.Now.ToString("O");
    public string GeneratedBy { get; set; } = "TeaChest";
    public string SourceComputer { get; set; } = Environment.MachineName;
    public string WindowsVersion { get; set; } = Environment.OSVersion.VersionString;
    public ManifestStats Stats { get; set; } = new();
    public List<AppRecord> Apps { get; set; } = new();

    /// <summary>Problems found while validating a loaded manifest. Not saved.</summary>
    [JsonIgnore]
    public List<string> LoadWarnings { get; set; } = new();
}

public sealed class ManifestStats
{
    public int Detected { get; set; }
    public int Selected { get; set; }
}

public sealed class AppRecord
{
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string InstallType { get; set; } = "manual";

    /// <summary>How sure the name lookup was. Older manifests have no value, which means None.</summary>
    [JsonConverter(typeof(MatchConfidenceJsonConverter))]
    public MatchConfidence MatchConfidence { get; set; }

    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string Category { get; set; } = "";
    public bool Include { get; set; } = true;
    public bool? DefaultInclude { get; set; }
    public string Notes { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string StoreSearchName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public string InstallerArgs { get; set; } = "";
    public string WingetArgs { get; set; } = "";

    public AppItem ToItem()
    {
        return new AppItem
        {
            Name = string.IsNullOrWhiteSpace(Name) ? "(unnamed)" : Name,
            Id = Id,
            Source = Source,
            InstallType = string.IsNullOrWhiteSpace(InstallType) ? InferInstallType(Source, Id) : InstallType,
            MatchConfidence = MatchConfidence,
            Version = Version,
            Publisher = Publisher,
            Scope = Scope,
            PackageFamilyName = PackageFamilyName,
            PackageFullName = PackageFullName,
            Category = Category,
            Notes = Notes,
            UninstallString = UninstallString,
            InstallLocation = InstallLocation,
            StoreSearchName = string.IsNullOrWhiteSpace(StoreSearchName) ? Name : StoreSearchName,
            DownloadUrl = DownloadUrl,
            Sha256 = Sha256,
            InstallerType = InstallerType,
            InstallerArgs = InstallerArgs,
            WingetArgs = WingetArgs,
            IsSelected = Include,
            DefaultSelected = DefaultInclude ?? Include
        };
    }

    public static AppRecord FromItem(AppItem item)
    {
        return new AppRecord
        {
            Name = item.Name,
            Id = item.Id,
            Source = item.Source,
            InstallType = item.InstallType,
            MatchConfidence = item.MatchConfidence,
            Version = item.Version,
            Publisher = item.Publisher,
            Scope = item.Scope,
            PackageFamilyName = item.PackageFamilyName,
            PackageFullName = item.PackageFullName,
            Category = item.Category,
            Include = item.IsSelected,
            DefaultInclude = item.DefaultSelected,
            Notes = item.Notes,
            UninstallString = item.UninstallString,
            InstallLocation = item.InstallLocation,
            StoreSearchName = item.StoreSearchName,
            DownloadUrl = item.DownloadUrl,
            Sha256 = item.Sha256,
            InstallerType = item.InstallerType,
            InstallerArgs = item.InstallerArgs,
            WingetArgs = item.WingetArgs
        };
    }

    private static string InferInstallType(string? source, string? id)
    {
        if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)) return "msstore";
        if (string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase)) return "winget";
        if (!string.IsNullOrEmpty(id) && id.StartsWith("9", StringComparison.Ordinal) && id.Length == 12) return "msstore";
        return "manual";
    }
}

public sealed class PlanItem : INotifyPropertyChanged
{
    private string _status = "";
    private string _statusDetail = "";

    public string Action { get; set; } = "";
    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Detail { get; set; } = "";
    public AppItem App { get; set; } = new();

    /// <summary>Empty until this row is attempted, then Installing or the final result.</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusLabel));
        }
    }

    /// <summary>What the Status column shows. Status itself stays the raw result the triggers match on.</summary>
    public string StatusLabel => Status == "RebootRequired" ? "Restart needed" : Status;

    public string StatusDetail
    {
        get => _statusDetail;
        set
        {
            if (_statusDetail == value) return;
            _statusDetail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class ScanSnapshot
{
    public IReadOnlyList<AppItem> Apps { get; init; } = Array.Empty<AppItem>();
    public string Status { get; init; } = "";
    public bool IsComplete { get; init; }
    public double Progress { get; init; }
    public bool IsIndeterminate { get; init; }
}

public sealed class InstallProgress
{
    public int Current { get; init; }
    public int Total { get; init; }
    public string AppName { get; init; } = "";
    public string Status { get; init; } = "";
    public string Detail { get; init; } = "";
    public string? Line { get; init; }
    public bool IsComplete { get; init; }

    /// <summary>The plan row this report belongs to, so the view can update it without a lookup.</summary>
    public PlanItem? Item { get; init; }
}
