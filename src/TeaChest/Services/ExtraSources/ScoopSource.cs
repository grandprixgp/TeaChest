using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>Apps installed with Scoop, as listed by scoop export.</summary>
internal sealed class ScoopSource : IExtraSource
{
    internal const string Command = "scoop";
    internal static readonly string[] ListArguments = { "export" };

    // The card's ^[\w.\-+]+$, except it can't start with '-' (so the note never holds a flag) and
    // \z stops a trailing newline slipping past $. A name that fails it is dropped, row and all.
    private static readonly Regex AppName = new(@"^\w[\w.\-+]*\z", RegexOptions.CultureInvariant);

    private readonly Func<string, string[], CancellationToken, Task<ProcessResult>> _run;

    public ScoopSource() : this(ExtraSourceTools.RunShimAsync)
    {
    }

    // Tests hand in canned output here, so they never run the real scoop command.
    internal ScoopSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run)
    {
        _run = run;
    }

    public string Name => "Scoop";

    public async Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _run(Command, ListArguments, cancellationToken).ConfigureAwait(false);
        // A cancelled run looks like a missing tool, and the scan should stop rather than report none.
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtraSourceTools.IsMissingTool(result)) return Array.Empty<AppItem>();
        if (result.TimedOut) throw new TimeoutException("scoop export didn't answer in time.");
        if (result.ExitCode != 0) throw new InvalidOperationException($"scoop export stopped with exit code {result.ExitCode}.");
        return Parse(result.Output);
    }

    /// <summary>
    /// Reads the "apps" list from scoop export's JSON. Older Scoop prints plain lines instead,
    /// which give nothing. The JSON is read as it goes, so output cut off partway still gives
    /// back every app that finished before the break.
    /// </summary>
    internal static List<AppItem> Parse(string? output)
    {
        var items = new List<AppItem>();
        string? json = FindJson(output);
        if (json == null) return items;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json), new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return items;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isApps = reader.ValueTextEquals("apps") || reader.ValueTextEquals("Apps");
                reader.Read();
                if (isApps && reader.TokenType == JsonTokenType.StartArray)
                {
                    ReadApps(ref reader, items, seen);
                }
                else
                {
                    reader.Skip();
                }
            }
        }
        catch (JsonException)
        {
            // Cut off or garbled partway: keep what finished before the break.
        }
        return items;
    }

    private static void ReadApps(ref Utf8JsonReader reader, List<AppItem> items, HashSet<string> seen)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string? name = null;
            string? version = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string property = reader.GetString() ?? "";
                reader.Read();
                if (reader.TokenType == JsonTokenType.String && property.Equals("Name", StringComparison.OrdinalIgnoreCase))
                {
                    name = reader.GetString();
                }
                else if (reader.TokenType == JsonTokenType.String && property.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    version = reader.GetString();
                }
                else
                {
                    reader.Skip();
                }
            }
            // Only an app whose closing brace arrived counts, so a cut-off one isn't half-read.
            if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Unfinished app entry.");

            name = name?.Trim();
            if (name == null || !AppName.IsMatch(name) || !seen.Add(name)) continue;
            items.Add(new AppItem
            {
                Name = name,
                Version = string.IsNullOrWhiteSpace(version) ? "" : version.Trim(),
                Notes = "scoop install " + name
            });
        }
    }

    // Scoop can print a warning before the JSON, so start at the first line that opens an object.
    private static string? FindJson(string? output)
    {
        if (string.IsNullOrEmpty(output)) return null;
        int start = 0;
        while (start < output.Length)
        {
            int end = output.IndexOf('\n', start);
            string line = (end < 0 ? output[start..] : output[start..end]).TrimStart('﻿', ' ', '\t', '\r');
            if (line.StartsWith('{')) return output[start..].TrimStart('﻿', ' ', '\t', '\r');
            if (end < 0) break;
            start = end + 1;
        }
        return null;
    }
}
