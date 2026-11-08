using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TeaChest.Models;

/// <summary>How much a package ID found by name can be trusted.</summary>
public enum MatchConfidence
{
    /// <summary>
    /// No name guess was involved, because there is either no ID yet or the ID came from
    /// WinGet's own inventory rather than a lookup.
    /// </summary>
    None,

    /// <summary>The package name matched the app name exactly.</summary>
    Exact,

    /// <summary>The name matched a shortened or version-insensitive candidate, so it is a guess.</summary>
    Loose
}

/// <summary>
/// Reads confidence leniently: it is only a display hint, and a newer TeaChest may write a value
/// this version does not know. Anything unrecognised becomes None instead of failing the file.
/// </summary>
internal sealed class MatchConfidenceJsonConverter : JsonConverter<MatchConfidence>
{
    public override bool HandleNull => true;

    public override MatchConfidence Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            string? name = reader.GetString();
            foreach (MatchConfidence value in Enum.GetValues<MatchConfidence>())
            {
                if (string.Equals(value.ToString(), name, StringComparison.OrdinalIgnoreCase)) return value;
            }
        }
        else if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            // Consume the whole value so the records after it still line up.
            reader.Skip();
        }
        return MatchConfidence.None;
    }

    public override void Write(Utf8JsonWriter writer, MatchConfidence value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public sealed class AppItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public string Name { get; set; } = "";
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string InstallType { get; set; } = "manual";
    public MatchConfidence MatchConfidence { get; set; }
    public string Version { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Scope { get; set; } = "";
    public string PackageFamilyName { get; set; } = "";
    public string PackageFullName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Notes { get; set; } = "";
    public string UninstallString { get; set; } = "";
    public string InstallLocation { get; set; } = "";
    public string StoreSearchName { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string InstallerType { get; set; } = "";
    public string InstallerArgs { get; set; } = "";
    public string WingetArgs { get; set; } = "";
    public bool DefaultSelected { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public bool IsLooseMatch => MatchConfidence == MatchConfidence.Loose;
    public bool IsManual => string.Equals(InstallType, "manual", StringComparison.OrdinalIgnoreCase);
    public bool IsStore => string.Equals(InstallType, "msstore", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Source, "msix", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Source, "msstore", StringComparison.OrdinalIgnoreCase)
                           || Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase);
    public bool IsWinget => string.Equals(InstallType, "winget", StringComparison.OrdinalIgnoreCase);
    public bool IsDownload => string.Equals(InstallType, "download", StringComparison.OrdinalIgnoreCase);
    public string TypeLabel => InstallType switch
    {
        "winget" => "WinGet",
        "msstore" => "Store",
        "download" => "Download",
        _ => "Manual"
    };

    public string SearchBlob => string.Join(' ', Name, Id, Category, InstallType, Publisher, Notes);

    public string IdentityKey => !string.IsNullOrWhiteSpace(Id) ? Id.ToLowerInvariant() : TextUtil.NormalizeLoose(Name);

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppItem Clone()
    {
        var clone = (AppItem)MemberwiseClone();
        clone.PropertyChanged = null;
        return clone;
    }

    public void MergeFrom(AppItem other)
    {
        bool hadId = !string.IsNullOrWhiteSpace(Id);
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(other.Name)) Name = other.Name;
        if (string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(other.Id)) Id = other.Id;
        if (string.IsNullOrWhiteSpace(Version) && !string.IsNullOrWhiteSpace(other.Version)) Version = other.Version;
        if (string.IsNullOrWhiteSpace(Publisher) && !string.IsNullOrWhiteSpace(other.Publisher)) Publisher = other.Publisher;
        if (string.IsNullOrWhiteSpace(Scope) && !string.IsNullOrWhiteSpace(other.Scope)) Scope = other.Scope;
        if (string.IsNullOrWhiteSpace(PackageFamilyName) && !string.IsNullOrWhiteSpace(other.PackageFamilyName)) PackageFamilyName = other.PackageFamilyName;
        if (string.IsNullOrWhiteSpace(PackageFullName) && !string.IsNullOrWhiteSpace(other.PackageFullName)) PackageFullName = other.PackageFullName;
        if (string.IsNullOrWhiteSpace(Category) && !string.IsNullOrWhiteSpace(other.Category)) Category = other.Category;
        if (string.IsNullOrWhiteSpace(Notes) && !string.IsNullOrWhiteSpace(other.Notes)) Notes = other.Notes;
        if (!hadId)
        {
            if (MatchConfidence == MatchConfidence.None && other.MatchConfidence != MatchConfidence.None) MatchConfidence = other.MatchConfidence;
        }
        else if (!string.IsNullOrWhiteSpace(other.Id) && string.Equals(Id, other.Id, StringComparison.OrdinalIgnoreCase))
        {
            // Confidence describes the ID, so a certain inventory ID must not become a guess just
            // because the same ID also arrived from a loose name lookup.
            if (ConfidenceStrength(other.MatchConfidence) > ConfidenceStrength(MatchConfidence)) MatchConfidence = other.MatchConfidence;
        }
        if (MatchConfidence == MatchConfidence.None && IsLooseNote(Notes))
        {
            // The ID is no longer a guess, so a note telling the user to check it would be wrong.
            // Fall back to the other row's note when that one still holds true.
            Notes = IsLooseNote(other.Notes) ? "" : other.Notes ?? "";
        }
        if (string.IsNullOrWhiteSpace(UninstallString) && !string.IsNullOrWhiteSpace(other.UninstallString)) UninstallString = other.UninstallString;
        if (string.IsNullOrWhiteSpace(InstallLocation) && !string.IsNullOrWhiteSpace(other.InstallLocation)) InstallLocation = other.InstallLocation;
        if (string.IsNullOrWhiteSpace(StoreSearchName) && !string.IsNullOrWhiteSpace(other.StoreSearchName)) StoreSearchName = other.StoreSearchName;
        if (string.IsNullOrWhiteSpace(DownloadUrl) && !string.IsNullOrWhiteSpace(other.DownloadUrl)) DownloadUrl = other.DownloadUrl;
        if (string.IsNullOrWhiteSpace(Sha256) && !string.IsNullOrWhiteSpace(other.Sha256)) Sha256 = other.Sha256;
        if (string.IsNullOrWhiteSpace(InstallerType) && !string.IsNullOrWhiteSpace(other.InstallerType)) InstallerType = other.InstallerType;
        if (string.IsNullOrWhiteSpace(InstallerArgs) && !string.IsNullOrWhiteSpace(other.InstallerArgs)) InstallerArgs = other.InstallerArgs;
        if (string.IsNullOrWhiteSpace(WingetArgs) && !string.IsNullOrWhiteSpace(other.WingetArgs)) WingetArgs = other.WingetArgs;
        if (TypePriority(other) > TypePriority(this))
        {
            if (other.IsWinget && string.IsNullOrWhiteSpace(other.Id)) return;
            Source = other.Source;
            InstallType = other.InstallType;
            if (!string.IsNullOrWhiteSpace(other.Id)) Id = other.Id;
            if (!string.IsNullOrWhiteSpace(other.Name) && other.Name.Length < Name.Length) Name = other.Name;
        }
    }

    // ScanService ends both of its loose-match notes with this sentence.
    private const string LooseNoteMarker = "Worth a quick check.";

    private static bool IsLooseNote(string? notes) => notes?.Contains(LooseNoteMarker, StringComparison.Ordinal) == true;

    private static int ConfidenceStrength(MatchConfidence value) => value switch
    {
        MatchConfidence.None => 2,
        MatchConfidence.Exact => 1,
        _ => 0
    };

    public static int TypePriority(AppItem item)
    {
        if (item.IsWinget) return 4;
        if (string.Equals(item.InstallType, "msstore", StringComparison.OrdinalIgnoreCase)) return 3;
        if (item.IsStore) return 2;
        return 1;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
