using System.Runtime.InteropServices;

namespace YTDownloadTool.Core.Process;

/// <summary>
/// Sends a genuine Windows CTRL_C_EVENT to a child console process (such as yt-dlp or ffmpeg)
/// started in its own hidden console (CreateNoWindow = true).
///
/// Works safely from a consoleless WPF WinExe process via a serialized AttachConsole ->
/// SetConsoleCtrlHandler(NULL, TRUE) -> GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0) -> FreeConsole
/// sequence, and delegates to the packaged WinExe helper when invoked from a console host
/// (such as dotnet test) so the parent host's own console is never disrupted.
/// </summary>
public static class ConsoleGracefulStopper
{
    public const string InternalSignalFlag = "--internal-signal-ctrl-c";

    private const uint CtrlCEvent = 0;
    private const int SignalDispatchDelayMs = 200;

    private static readonly SemaphoreSlim ConsoleAttachLock = new(1, 1);

    /// <summary>
    /// Entry point invoked when the WinExe host is spawned in isolated helper mode
    /// (<c>--internal-signal-ctrl-c &lt;pid&gt;</c>). Returns true if the flag matched and was handled.
    /// </summary>
    public static bool TryHandleInternalSignalArgs(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length != 2 || !string.Equals(args[0], InternalSignalFlag, StringComparison.Ordinal))
            return false;

        if (!int.TryParse(args[1], out var pid) || pid <= 0)
        {
            exitCode = 2;
            return true;
        }

        exitCode = SendCtrlCInCurrentConsolelessProcess(pid) ? 0 : 1;
        return true;
    }

    public static async Task<bool> SendCtrlCAsync(int targetProcessId, string? winExeSignalerPath = null, CancellationToken ct = default)
    {
        if (targetProcessId <= 0)
            return false;

        // If our own process has NO attached console (standard WPF WinExe runtime),
        // we can safely attach directly to the child's private hidden console under lock.
        if (!ProcessHasConsole())
        {
            await ConsoleAttachLock.WaitAsync(ct);
            try
            {
                return SendCtrlCInCurrentConsolelessProcess(targetProcessId);
            }
            finally
            {
                ConsoleAttachLock.Release();
            }
        }

        // If the parent host ALREADY has a console (e.g., dotnet test / console host),
        // avoid calling FreeConsole() on the host; instead signal via the WinExe helper process.
        if (!string.IsNullOrWhiteSpace(winExeSignalerPath) && File.Exists(winExeSignalerPath))
        {
            return await InvokeWinExeSignalerAsync(winExeSignalerPath, targetProcessId, ct);
        }

        return false;
    }

    private static bool ProcessHasConsole()
    {
        if (GetConsoleWindow() != IntPtr.Zero)
            return true;

        var buffer = new uint[1];
        return GetConsoleProcessList(buffer, 1) > 0;
    }

    internal static bool SendCtrlCInCurrentConsolelessProcess(int targetProcessId)
    {
        FreeConsole();

        if (!AttachConsole((uint)targetProcessId))
            return false;

        var handlerDisabled = false;
        try
        {
            handlerDisabled = SetConsoleCtrlHandler(IntPtr.Zero, true);
            if (!handlerDisabled)
                return false;

            var sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
            if (sent)
            {
                Thread.Sleep(SignalDispatchDelayMs);
            }

            return sent;
        }
        finally
        {
            FreeConsole();
            if (handlerDisabled)
            {
                SetConsoleCtrlHandler(IntPtr.Zero, false);
            }
        }
    }

    private static async Task<bool> InvokeWinExeSignalerAsync(string exePath, int targetPid, CancellationToken ct)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add(InternalSignalFlag);
            psi.ArgumentList.Add(targetPid.ToString());

            using var helper = System.Diagnostics.Process.Start(psi);
            if (helper is null)
                return false;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));
            await helper.WaitForExitAsync(timeoutCts.Token);
            return helper.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetConsoleProcessList([Out] uint[] lpdwProcessList, uint dwProcessCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handlerRoutine, [MarshalAs(UnmanagedType.Bool)] bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
