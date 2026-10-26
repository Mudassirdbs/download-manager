using System.Diagnostics;
using System.Text;

namespace DownloadManager.Core;

public sealed record ProcessResult(int ExitCode, string Stdout, string StderrTail);

/// <summary>Runs a console tool with argument-list quoting, UTF-8 pipes and process-tree kill on cancellation.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string exe, IEnumerable<string> args, CancellationToken token,
        Action<string>? onStdoutLine = null, bool captureStdout = false)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8"; // yt-dlp prints titles in the console code page otherwise
        psi.Environment["PYTHONUTF8"] = "1";

        using var process = Process.Start(psi) ?? throw new IOException($"Could not start {exe}");
        var stdout = new StringBuilder();
        var tail = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (captureStdout) lock (stdout) stdout.AppendLine(e.Data);
            onStdoutLine?.Invoke(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (tail)
            {
                tail.Enqueue(e.Data);
                if (tail.Count > 30) tail.Dequeue();
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            // yt-dlp runs ffmpeg as a child; stop the whole tree so pausing frees the files.
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }
        process.WaitForExit(); // drains the async readers
        string err;
        lock (tail) err = string.Join("\n", tail);
        return new ProcessResult(process.ExitCode, stdout.ToString(), err);
    }

    /// <summary>The most useful line of a tool's stderr, e.g. yt-dlp's "ERROR: …".</summary>
    public static string Summarize(string stderrTail, string fallback)
    {
        var lines = stderrTail.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)) ?? lines.LastOrDefault() ?? fallback;
    }
}
