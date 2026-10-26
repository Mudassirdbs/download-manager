using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using DownloadManager.Core;

// Chrome/Firefox native messaging host. The browser starts one instance per extension connection and talks
// over stdin/stdout; this process relays every frame unchanged to the desktop app's named pipe and back.
// stdout carries protocol frames only, so diagnostics go to stderr (the browser logs it).

Log.Init("host");
using var stdin = Console.OpenStandardInput();
using var stdout = Console.OpenStandardOutput();
using var cts = new CancellationTokenSource();

// The extension's first frame says whether a missing app may be started (explicit user action) or not
// (background reconnect at browser start).
var first = await Framing.ReadFrameAsync(stdin, cts.Token);
if (first is null) return 0;
var launch = TryDecode(first)?.Launch != false;

NamedPipeClientStream? pipe;
try
{
    pipe = await ConnectAsync(launch, cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"dmhost: cannot reach DownloadManager: {ex.Message}");
    Log.Warn($"Cannot reach DownloadManager: {ex.Message}");
    pipe = null;
}
if (pipe is null)
{
    await Framing.WriteFrameAsync(stdout, Framing.Encode(new BridgeMessage
    {
        Type = "error",
        Error = launch ? "app_unavailable" : "app_not_running",
    }), CancellationToken.None);
    return 0;
}

await using (pipe)
{
    await Framing.WriteFrameAsync(pipe, first, cts.Token);
    var up = Relay(stdin, pipe, cts.Token);
    var down = Relay(pipe, stdout, cts.Token);
    await Task.WhenAny(up, down);
    cts.Cancel();
}
return 0;

static async Task Relay(Stream from, Stream to, CancellationToken token)
{
    try
    {
        while (await Framing.ReadFrameAsync(from, token) is { } frame)
            await Framing.WriteFrameAsync(to, frame, token);
    }
    catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException)
    {
        // Either side closed: the browser disconnected or the app exited.
    }
}

static BridgeMessage? TryDecode(byte[] frame)
{
    try { return Framing.Decode(frame); }
    catch (System.Text.Json.JsonException) { return null; }
}

static async Task<NamedPipeClientStream?> ConnectAsync(bool launch, CancellationToken token)
{
    var pipe = NewPipe();
    try
    {
        await pipe.ConnectAsync(300, token);
        return pipe;
    }
    catch (TimeoutException)
    {
        pipe.Dispose();
    }
    if (!launch) return null;

    LaunchApp();
    var deadline = DateTime.UtcNow.AddSeconds(20);
    while (true)
    {
        pipe = NewPipe();
        try
        {
            await pipe.ConnectAsync(1000, token);
            return pipe;
        }
        catch (TimeoutException) when (DateTime.UtcNow < deadline)
        {
            pipe.Dispose();
        }
    }
}

static NamedPipeClientStream NewPipe() => new(".", Bridge.PipeName, PipeDirection.InOut,
    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

static void LaunchApp()
{
    var exe = Path.Combine(AppContext.BaseDirectory, "DownloadManager.exe");
    if (!File.Exists(exe)) throw new FileNotFoundException("DownloadManager.exe not found next to dmhost.exe", exe);
    // The browser runs hosts inside a job object that is closed with the host. Break away so the app
    // keeps running after the browser disconnects; fall back to a normal child if breakaway is denied.
    if (!Native.TryStartBrokenAway(exe, "--background"))
        Process.Start(new ProcessStartInfo(exe, "--background") { UseShellExecute = false });
}

static class Native
{
    const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;
    const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    const int ERROR_ACCESS_DENIED = 5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string? app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    public static bool TryStartBrokenAway(string exe, string args)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        if (!CreateProcessW(exe, $"\"{exe}\" {args}", IntPtr.Zero, IntPtr.Zero, false,
                CREATE_BREAKAWAY_FROM_JOB | CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero, Path.GetDirectoryName(exe), ref si, out var pi))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ERROR_ACCESS_DENIED) return false;
            throw new Win32Exception(error);
        }
        CloseHandle(pi.hThread);
        CloseHandle(pi.hProcess);
        return true;
    }
}
