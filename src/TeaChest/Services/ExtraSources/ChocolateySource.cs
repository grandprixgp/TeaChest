using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>Packages installed with Chocolatey, as listed by choco list.</summary>
internal sealed class ChocolateySource : IExtraSource
{
    internal const string Command = "choco";
    internal static readonly string[] VersionArguments = { "--version" };
    internal static readonly string[] ListArguments = { "list", "--limit-output" };
    // Before 2.0, plain "choco list" searched the community feed rather than this PC, and 2.0
    // refuses --local-only, so which one to send depends on the version.
    internal static readonly string[] LegacyListArguments = { "list", "--local-only", "--limit-output" };

    // With useEnhancedExitCodes turned on, choco list says 2 when nothing is installed.
    private const int NothingFound = 2;

    // Same rule as Scoop: the card's ^[\w.\-+]+$, minus a leading '-' so the note never holds a
    // flag, and \z so a trailing newline can't slip past $. A name that fails it is dropped.
    private static readonly Regex PackageName = new(@"^\w[\w.\-+]*\z", RegexOptions.CultureInvariant);

    private readonly Func<string, string[], CancellationToken, Task<ProcessResult>> _run;

    public ChocolateySource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real choco command.
    internal ChocolateySource(Func<string, string[], CancellationToken, Task<ProcessResult>> run)
    {
        _run = run;
    }

    public string Name => "Chocolatey";

    public async Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessResult version = await _run(Command, VersionArguments, cancellationToken).ConfigureAwait(false);
        // A cancelled run looks like a missing tool, and the scan should stop rather than report none.
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtraSourceTools.IsMissingTool(version)) return Array.Empty<AppItem>();
        if (version.TimedOut) throw new TimeoutException("choco --version didn't answer in time.");

        string[] arguments = IsLegacy(version) ? LegacyListArguments : ListArguments;
        ProcessResult result = await _run(Command, arguments, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtraSourceTools.IsMissingTool(result)) return Array.Empty<AppItem>();
        if (result.TimedOut) throw new TimeoutException("choco list didn't answer in time.");
        if (result.ExitCode != 0 && result.ExitCode != NothingFound)
            throw new InvalidOperationException($"choco list stopped with exit code {result.ExitCode}.");
        return Parse(result.Output);
    }

    /// <summary>
    /// True for Chocolatey before 2.0. Anything that can't be read as a version counts as current,
    /// since that's what almost everyone has and the 2.0 form can't reach the network.
    /// </summary>
    internal static bool IsLegacy(ProcessResult version)
    {
        if (version.ExitCode != 0 || string.IsNullOrWhiteSpace(version.Output)) return false;
        foreach (string raw in version.Output.Split('\n'))
        {
            Match match = Regex.Match(raw.Trim(), @"^v?(\d+)\.\d+", RegexOptions.CultureInvariant);
            if (match.Success) return int.TryParse(match.Groups[1].Value, out int major) && major < 2;
        }
        return false;
    }

    /// <summary>
    /// Reads "name|version" lines. Anything else is skipped, so odd output gives back whatever did
    /// parse instead of failing. Chocolatey itself is left out, since it's the installer, not an app.
    /// </summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(output)) return items;

        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            string[] parts = line.Split('|');
            if (parts.Length != 2) continue;

            string name = parts[0].Trim();
            string version = parts[1].Trim();
            if (version.Length == 0 || version.Any(char.IsWhiteSpace)) continue;
            if (!PackageName.IsMatch(name)) continue;
            if (name.Equals("chocolatey", StringComparison.OrdinalIgnoreCase)) continue;
            if (!seen.Add(name)) continue;

            items.Add(new AppItem
            {
                Name = name,
                Version = version,
                Notes = "choco install " + name
            });
        }
        return items;
    }
}
