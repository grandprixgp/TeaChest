using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>Packages installed globally with npm, as listed by npm ls -g.</summary>
internal sealed class NpmSource : DevToolsSource
{
    internal const string ShimCommand = "npm";
    internal static readonly string[] ListArguments = { "ls", "-g", "--depth=0", "--json" };

    // The shared rule, with an optional @scope/ in front, which is the one place npm allows a '/'.
    private static readonly Regex NpmName = new(@"^(?:@\w[\w.\-]*/)?\w[\w.\-]*\z", RegexOptions.CultureInvariant);

    // npm comes with Node and corepack with older Node, so reinstalling either on its own makes no sense.
    private static readonly HashSet<string> Bundled = new(StringComparer.OrdinalIgnoreCase) { "npm", "corepack" };

    public NpmSource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real npm command.
    internal NpmSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run) : base(run)
    {
    }

    public override string Name => "npm";
    protected override string Command => ShimCommand;
    protected override string[] Arguments => ListArguments;
    protected override string Describe => "npm ls";

    protected override List<AppItem> ParseOutput(string? output) => Parse(output);

    // npm ls exits 1 whenever it spots a problem in the tree, such as a missing peer dependency,
    // and still prints the full list, so that's only an error when nothing could be read.
    protected override IReadOnlyList<AppItem> OnFailedExit(ProcessResult result)
    {
        List<AppItem> items = Parse(result.Output);
        return items.Count > 0 ? items : base.OnFailedExit(result);
    }

    /// <summary>Reads the "dependencies" object from npm ls --json, leaving out npm and corepack.</summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ReadEntries(output, "dependencies", (name, entry) =>
        {
            // No trimming: a key with stray whitespace in it is odd output, and it's dropped.
            if (!NpmName.IsMatch(name) || Bundled.Contains(name) || !seen.Add(name)) return;
            items.Add(Item(name, StringAt(entry, "version"), "npm install -g " + name));
        });
        return items;
    }
}
