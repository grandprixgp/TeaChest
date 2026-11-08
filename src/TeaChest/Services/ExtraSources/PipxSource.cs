using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>Python apps installed with pipx, as listed by pipx list --json.</summary>
internal sealed class PipxSource : DevToolsSource
{
    internal const string ShimCommand = "pipx";
    internal static readonly string[] ListArguments = { "list", "--json" };

    public PipxSource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real pipx command.
    internal PipxSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run) : base(run)
    {
    }

    public override string Name => "pipx";
    protected override string Command => ShimCommand;
    protected override string[] Arguments => ListArguments;
    protected override string Describe => "pipx list";

    protected override List<AppItem> ParseOutput(string? output) => Parse(output);

    /// <summary>Reads the "venvs" object from pipx list --json, one row per venv.</summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadEntries(output, "venvs", (name, entry) =>
        {
            // No trimming: a key with stray whitespace in it is odd output, and it's dropped.
            if (!PackageName.IsMatch(name) || !seen.Add(name)) return;
            string? version = StringAt(entry, "metadata", "main_package", "package_version");
            items.Add(Item(name, version, "pipx install " + name));
        });
        return items;
    }
}
