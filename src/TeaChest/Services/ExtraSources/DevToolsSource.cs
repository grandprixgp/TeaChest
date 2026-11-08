using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TeaChest.Models;

namespace TeaChest.Services.ExtraSources;

/// <summary>
/// What the global npm, pipx, and dotnet tool sources share. They're three classes rather than
/// one because ExtraSourceTools.Prepare takes a row's Source and prefix from the source's Name,
/// so one class can only ever report one source.
/// </summary>
internal abstract class DevToolsSource : IExtraSource
{
    // The same rule as Scoop and Chocolatey: can't start with '-', so the note never holds a
    // flag, and \z stops a trailing newline slipping past $. A name that fails it is dropped.
    internal static readonly Regex PackageName = new(@"^\w[\w.\-]*\z", RegexOptions.CultureInvariant);

    private readonly Func<string, string[], CancellationToken, Task<ProcessResult>> _run;

    protected DevToolsSource(Func<string, string[], CancellationToken, Task<ProcessResult>> run)
    {
        _run = run;
    }

    public abstract string Name { get; }

    protected abstract string Command { get; }
    protected abstract string[] Arguments { get; }

    // Shown in errors, such as "npm ls" or "dotnet tool list".
    protected abstract string Describe { get; }

    protected abstract List<AppItem> ParseOutput(string? output);

    /// <summary>
    /// Decides what a non-zero exit means. By default it's an error. A source can say its tool
    /// exits non-zero for ordinary reasons and give back what it found, or nothing.
    /// </summary>
    protected virtual IReadOnlyList<AppItem> OnFailedExit(ProcessResult result)
        => throw new InvalidOperationException($"{Describe} stopped with exit code {result.ExitCode}.");

    public async Task<IReadOnlyList<AppItem>> ScanAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await _run(Command, Arguments, cancellationToken).ConfigureAwait(false);
        // A cancelled run looks like a missing tool, and the scan should stop rather than report none.
        cancellationToken.ThrowIfCancellationRequested();
        if (ExtraSourceTools.IsMissingTool(result)) return Array.Empty<AppItem>();
        if (result.TimedOut) throw new TimeoutException($"{Describe} didn't answer in time.");
        if (result.ExitCode != 0) return OnFailedExit(result);
        return ParseOutput(result.Output);
    }

    internal static AppItem Item(string name, string? version, string notes) => new()
    {
        Name = name,
        Version = CleanVersion(version),
        Notes = notes
    };

    // A version is only shown, never run, but one with spaces or control characters in it isn't a
    // version, so it's left blank rather than shown oddly.
    internal static string CleanVersion(string? version)
    {
        string trimmed = version?.Trim() ?? "";
        return trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ? "" : trimmed;
    }

    /// <summary>
    /// Finds the object under a top-level property (npm's "dependencies", pipx's "venvs") and
    /// hands each complete entry in it to <paramref name="onEntry"/> with its key. The JSON is read
    /// as it goes, so output cut off partway still gives every entry that finished before the
    /// break, and an entry that didn't finish is never half-read.
    /// </summary>
    internal static void ReadEntries(string? output, string property, Action<string, JsonElement> onEntry)
    {
        string? json = FindJson(output);
        if (json == null) return;

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool wanted = reader.ValueTextEquals(property);
                reader.Read();
                if (!wanted || reader.TokenType != JsonTokenType.StartObject)
                {
                    reader.Skip();
                    continue;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    string key = reader.GetString() ?? "";
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        reader.Skip();
                        continue;
                    }
                    int start = (int)reader.TokenStartIndex;
                    // Throws if the entry is cut off, which ends the read before it's handed on.
                    reader.Skip();
                    int end = (int)reader.BytesConsumed;
                    using JsonDocument entry = JsonDocument.Parse(bytes.AsMemory(start, end - start));
                    onEntry(key, entry.RootElement);
                }
            }
        }
        catch (JsonException)
        {
            // Cut off or garbled partway: keep what finished before the break.
        }
    }

    /// <summary>The string at a path of properties inside an entry, or null if any step is missing.</summary>
    internal static string? StringAt(JsonElement element, params string[] path)
    {
        foreach (string step in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(step, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    // Warnings can come before the JSON (stderr shares the output), so start at the first line
    // that opens an object.
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
