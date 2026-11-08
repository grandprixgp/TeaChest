using System.Diagnostics;
using System.IO;
using System.Text;

namespace TeaChest.Services;

public sealed record ProcessResult(int ExitCode, string Output, bool TimedOut);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        Action<string>? onLine,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        var output = new StringBuilder();
        var gate = new object();

        try
        {
            if (!process.Start()) return new ProcessResult(-1, "Could not start " + fileName, false);
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, ex.Message, false);
        }

        Task readOut = ReadStreamAsync(process.StandardOutput, onLine, output, gate);
        Task readErr = ReadStreamAsync(process.StandardError, onLine, output, gate);

        using var timeoutCts = new CancellationTokenSource();
        if (timeout is { } t && t > TimeSpan.Zero) timeoutCts.CancelAfter(t);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = !cancellationToken.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            try { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }

        try { await Task.WhenAll(readOut, readErr).ConfigureAwait(false); } catch { }

        string text;
        lock (gate) text = output.ToString();
        return new ProcessResult(process.HasExited ? process.ExitCode : -1, text, timedOut);
    }

    private static async Task ReadStreamAsync(StreamReader reader, Action<string>? onLine, StringBuilder output, object gate)
    {
        while (true)
        {
            string? line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null) break;
            lock (gate) output.AppendLine(line);
            if (onLine != null)
            {
                try { onLine(line); } catch { }
            }
        }
    }
}
