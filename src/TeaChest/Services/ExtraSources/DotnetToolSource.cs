using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>.NET global tools, as listed by dotnet tool list -g.</summary>
internal sealed class DotnetToolSource : DevToolsSource
{
    internal const string ShimCommand = "dotnet";
    internal static readonly string[] ListArguments = { "tool", "list", "-g" };

    // Every tool version starts with a digit, which keeps a stray line of prose from passing as a row.
    private static readonly Regex ToolVersion = new(@"^\d[\w.\-+]*\z", RegexOptions.CultureInvariant);

    public DotnetToolSource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real dotnet command.
    internal DotnetToolSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run) : base(run)
    {
    }

    // Lower-cased this is the row's Source, which AppClassifier.AddOnSources already lists.
    public override string Name => "dotnet tool";
    protected override string Command => ShimCommand;
    protected override string[] Arguments => ListArguments;
    protected override string Describe => "dotnet tool list";

    protected override List<AppItem> ParseOutput(string? output) => Parse(output);

    // Plenty of PCs have the .NET runtime but no SDK, and then dotnet tool fails. That isn't
    // something to warn about, any more than npm not being installed is.
    protected override IReadOnlyList<AppItem> OnFailedExit(ProcessResult result) => Array.Empty<AppItem>();

    /// <summary>
    /// Reads the table under the dashed line. The header above it is translated, so it's never
    /// read. The first run of dotnet prints a welcome banner with dashed lines of its own, so the
    /// table's is the last one.
    /// </summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        if (string.IsNullOrEmpty(output)) return items;

        string[] lines = output.Split('\n');
        int separator = Array.FindLastIndex(lines, IsSeparator);
        if (separator < 0) return items;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = separator + 1; i < lines.Length; i++)
        {
            string[] cells = lines[i].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 2) continue;

            string id = cells[0];
            string version = cells[1];
            if (!PackageName.IsMatch(id) || !ToolVersion.IsMatch(version) || !seen.Add(id)) continue;
            items.Add(Item(id, version, "dotnet tool install -g " + id));
        }
        return items;
    }

    private static bool IsSeparator(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= 8 && trimmed.All(c => c == '-');
    }
}
