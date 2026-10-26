namespace DownloadManager.Core;

public static class Ffmpeg
{
    /// <summary>Remuxes (no re-encoding) a video track and an optional separate audio track into MP4.</summary>
    public static async Task MuxAsync(string ffmpeg, string video, string? audio, string output, CancellationToken token)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video };
        if (audio is not null) args.AddRange(["-i", audio, "-map", "0:v:0?", "-map", "1:a:0"]);
        else args.AddRange(["-map", "0"]);
        // Copy streams as they are; mp4 needs ADTS AAC converted to ASC, which ffmpeg does automatically.
        args.AddRange(["-c", "copy", "-movflags", "+faststart", "-f", "mp4", output]);
        var result = await ProcessRunner.RunAsync(ffmpeg, args, token);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("ffmpeg could not merge the stream: " + ProcessRunner.Summarize(result.StderrTail, $"exit code {result.ExitCode}"));
    }
}
