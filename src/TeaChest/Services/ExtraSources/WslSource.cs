using System.IO;
using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>Linux distros installed under WSL, as listed by wsl.exe --list --verbose.</summary>
internal sealed class WslSource : IExtraSource
{
    internal static readonly string[] ListArguments = { "--list", "--verbose" };

    // The card's ^[\w.\-]+$, except it can't start with '-' (so the note never holds a flag) and
    // \z stops a trailing newline slipping past $. A name that fails it is dropped.
    private static readonly Regex DistroName = new(@"^\w[\w.\-]*\z", RegexOptions.CultureInvariant);

    // WSL itself only ever says 1 or 2 here, which tells a distro row from a stray message.
    private static readonly Regex WslVersion = new(@"^\d+\z", RegexOptions.CultureInvariant);

    // Docker Desktop and Rancher Desktop make these for themselves and make them again when
    // they're reinstalled. They aren't in the store, so wsl --install -d can't bring them back.
    private static readonly HashSet<string> MadeByOtherApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "docker-desktop", "docker-desktop-data", "rancher-desktop", "rancher-desktop-data"
    };

    private readonly Func<string, string[], CancellationToken, Task<ProcessResult>> _run;

    public WslSource() : this(RunWslAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real wsl.exe.
    internal WslSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run)
    {
        _run = run;
    }

    public string Name => "WSL";

    // The full path, so a wsl.exe somewhere else on PATH can't stand in for it.
    internal static string WslPath => Path.Combine(Environment.SystemDirectory, "wsl.exe");

    // wsl.exe is a real program rather than a .cmd shim, so it starts directly and doesn't need
    // cmd.exe. When it isn't there, ProcessRunner gives exit code -1, which counts as no WSL below.
    private static Task<ProcessResult> RunWslAsync(string command, string[] args, CancellationToken cancellationToken)
        => ProcessRunner.RunAsync(command, args, null, ExtraSourceTools.ShimTimeout, cancellationToken);

    public async Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _run(WslPath, ListArguments, cancellationToken).ConfigureAwait(false);
        // A cancelled run looks like a missing tool, and the scan should stop rather than report none.
        cancellationToken.ThrowIfCancellationRequested();
        if (result.TimedOut) throw new TimeoutException("wsl --list didn't answer in time.");
        // Without WSL, or with WSL but no distros, wsl.exe exits non-zero. Neither is worth a warning.
        if (result.ExitCode != 0) return Array.Empty<AppItem>();
        return Parse(result.Output);
    }

    /// <summary>
    /// Reads the distro table. wsl.exe writes UTF-16LE and ProcessRunner reads UTF-8, so the text
    /// arrives with a NUL after every character, and those go first. The header is translated, so
    /// it's skipped by position, and a row has to end in a WSL version number to count.
    /// </summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        if (string.IsNullOrEmpty(output)) return items;

        // Stripping NULs also leaves a blank line after each real one, which is skipped below. A
        // UTF-16 byte order mark, if one comes, reads as U+FFFD or U+FEFF and goes too.
        string text = output.Replace("\0", "").Replace("\uFFFD", "").Replace("\uFEFF", "");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool header = true;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (header)
            {
                header = false;
                continue;
            }

            // "* Ubuntu  Running  2": the * marks the default distro, and the state can be more than
            // one word once it's translated, so the name is the first cell and the version the last.
            if (line.StartsWith('*')) line = line[1..].TrimStart();
            string[] cells = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (cells.Length < 3 || !WslVersion.IsMatch(cells[^1])) continue;

            string name = cells[0];
            if (!DistroName.IsMatch(name) || MadeByOtherApps.Contains(name) || !seen.Add(name)) continue;

            items.Add(new AppItem
            {
                Name = name,
                // The distro's own version isn't in this list, and WSL's "2" would read as if it were.
                Version = "",
                Notes = "wsl --install -d " + name
            });
        }
        return items;
    }
}
