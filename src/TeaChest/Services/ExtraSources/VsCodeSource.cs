using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>VS Code extensions, as listed by the code command line.</summary>
internal sealed class VsCodeSource : IExtraSource
{
    internal const string Command = "code";
    internal static readonly string[] ListArguments = { "--list-extensions", "--show-versions" };

    // publisher.name. Anything else stays out of the reinstall command shown in the note. Unlike
    // the card's ^[\w-]+\.[\w-]+$ it can't start with '-', so the note never holds a flag.
    private static readonly Regex ExtensionId = new(@"^\w[\w-]*\.[\w-]+\z", RegexOptions.CultureInvariant);

    private readonly Func<string, string[], CancellationToken, Task<ProcessResult>> _run;

    public VsCodeSource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real code command.
    internal VsCodeSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run)
    {
        _run = run;
    }

    public string Name => "VS Code";

    public async Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _run(Command, ListArguments, cancellationToken).ConfigureAwait(false);
        // A cancelled run looks like a missing tool, and the scan should stop rather than report none.
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtraSourceTools.IsMissingTool(result)) return Array.Empty<AppItem>();
        if (result.TimedOut) throw new TimeoutException("code --list-extensions didn't answer in time.");
        if (result.ExitCode != 0) throw new InvalidOperationException($"code --list-extensions stopped with exit code {result.ExitCode}.");
        return Parse(result.Output);
    }

    /// <summary>
    /// Reads "publisher.name@1.2.3" lines. Anything else, such as a warning VS Code prints on the
    /// way, is skipped, so odd output gives back whatever did parse instead of failing.
    /// </summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return items;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int at = line.IndexOf('@');
            if (at <= 0 || at == line.Length - 1) continue;

            string id = line[..at];
            string version = line[(at + 1)..];
            if (id.Any(char.IsWhiteSpace) || version.Any(char.IsWhiteSpace) || version.Contains('@')) continue;
            if (!seen.Add(id)) continue;

            items.Add(new AppItem
            {
                Name = id,
                Version = version,
                Category = "Development",
                Notes = ExtensionId.IsMatch(id)
                    ? "code --install-extension " + id
                    : "Reinstall it from the Extensions view in VS Code."
            });
        }
        return items;
    }
}
