using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace YTDownloadTool.Core.Process;

public enum GracefulStopStrategy
{
    /// <summary>Immediately kill the entire process tree on cancellation.</summary>
    KillImmediate = 0,

    /// <summary>Send Windows CTRL_C_EVENT and wait up to GracefulStopTimeout before force-killing.</summary>
    CtrlC = 1,

    /// <summary>Send 'q\n' to StandardInput (FFmpeg graceful quit), fall back to CTRL_C_EVENT, then force-kill.</summary>
    StdinQuitThenCtrlC = 2
}

public sealed class ProcessRunRequest
{
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }
    public GracefulStopStrategy GracefulStop { get; init; } = GracefulStopStrategy.CtrlC;
    public TimeSpan GracefulStopTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public bool AttachToJobObject { get; init; } = true;
    public Action<string>? OnStandardOutput { get; init; }
    public Action<string>? OnStandardError { get; init; }
    public Action<int>? OnProcessStarted { get; init; }
}

public sealed record ProcessRunResult(
    int ExitCode,
    bool WasCancelled,
    bool GracefulStopUsed,
    bool ForceKilled,
    TimeSpan Elapsed,
    string StandardOutput,
    string StandardError);

public interface IProcessRunner : IDisposable
{
    Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default);
}

public sealed class SafeProcessRunner : IProcessRunner
{
    private readonly ILogger<SafeProcessRunner> _logger;
    private readonly WindowsJobObject _jobObject;
    private readonly string? _winExeSignalerPath;
    private bool _disposed;

    public SafeProcessRunner(ILogger<SafeProcessRunner> logger, string? winExeSignalerPath = null)
    {
        _logger = logger;
        _jobObject = new WindowsJobObject();
        _winExeSignalerPath = winExeSignalerPath;
    }

    public async Task<ProcessRunResult> RunAsync(ProcessRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(request.ExecutablePath))
            throw new ArgumentException("Executable path must not be empty.", nameof(request));

        var enableStdin = request.GracefulStop == GracefulStopStrategy.StdinQuitThenCtrlC;

        var startInfo = new ProcessStartInfo
        {
            FileName = request.ExecutablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = enableStdin,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
            startInfo.WorkingDirectory = request.WorkingDirectory;

        foreach (var arg in request.Arguments)
            startInfo.ArgumentList.Add(arg);

        if (request.EnvironmentVariables is not null)
        {
            foreach (var (key, value) in request.EnvironmentVariables)
                startInfo.Environment[key] = value;
        }

        var sw = Stopwatch.StartNew();
        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        _logger.LogDebug("Starting process: {Exe} (ArgCount={Count})", Path.GetFileName(request.ExecutablePath), request.Arguments.Count);

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{request.ExecutablePath}'.");

        if (request.AttachToJobObject)
        {
            if (!_jobObject.TryAssignProcess(process))
            {
                _logger.LogDebug("Process {Pid} exited before Job Object assignment or could not be assigned.", process.Id);
            }
        }

        request.OnProcessStarted?.Invoke(process.Id);

        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        // Drain stdout and stderr concurrently to prevent pipe deadlocks.
        var stdOutTask = ReadLinesAsync(process.StandardOutput, request.OnStandardOutput, stdOutBuffer);
        var stdErrTask = ReadLinesAsync(process.StandardError, request.OnStandardError, stdErrBuffer);

        var wasCancelled = false;
        var gracefulStopUsed = false;
        var forceKilled = false;

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            wasCancelled = true;
            _logger.LogInformation("Cancellation requested for process {Exe} (PID={Pid}); strategy={Strategy}",
                Path.GetFileName(request.ExecutablePath), process.Id, request.GracefulStop);

            (gracefulStopUsed, forceKilled) = await StopProcessAsync(process, request);
        }

        // Wait briefly for stream readers to flush remaining buffered lines.
        try
        {
            await Task.WhenAll(stdOutTask, stdErrTask).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Timed out draining stdout/stderr for PID={Pid}.", process.Id);
        }

        sw.Stop();

        var exitCode = -1;
        try
        {
            if (process.HasExited)
                exitCode = process.ExitCode;
        }
        catch
        {
            // Ignore if handle already closed.
        }

        return new ProcessRunResult(
            ExitCode: exitCode,
            WasCancelled: wasCancelled,
            GracefulStopUsed: gracefulStopUsed,
            ForceKilled: forceKilled,
            Elapsed: sw.Elapsed,
            StandardOutput: stdOutBuffer.ToString(),
            StandardError: stdErrBuffer.ToString());
    }

    private async Task<(bool GracefulUsed, bool ForceKilled)> StopProcessAsync(System.Diagnostics.Process process, ProcessRunRequest request)
    {
        if (SafeHasExited(process))
            return (false, false);

        if (request.GracefulStop == GracefulStopStrategy.KillImmediate)
        {
            TryKillTree(process);
            return (false, true);
        }

        var timeout = request.GracefulStopTimeout > TimeSpan.Zero
            ? request.GracefulStopTimeout
            : TimeSpan.FromSeconds(8);

        var gracefulUsed = false;

        // Phase 1: Stdin 'q\n' for FFmpeg interactive quit
        if (request.GracefulStop == GracefulStopStrategy.StdinQuitThenCtrlC)
        {
            try
            {
                await process.StandardInput.WriteLineAsync("q");
                await process.StandardInput.FlushAsync();
                gracefulUsed = true;

                if (await WaitForExitWithTimeoutAsync(process, TimeSpan.FromMilliseconds(timeout.TotalMilliseconds / 2)))
                    return (true, false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not send stdin 'q' to PID={Pid}; falling back to CTRL_C.", process.Id);
            }
        }

        // Phase 2: Send CTRL_C_EVENT to child's hidden console
        if (!SafeHasExited(process))
        {
            try
            {
                var sent = await ConsoleGracefulStopper.SendCtrlCAsync(process.Id, _winExeSignalerPath, CancellationToken.None);
                if (sent)
                {
                    gracefulUsed = true;
                    if (await WaitForExitWithTimeoutAsync(process, timeout))
                        return (true, false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "CTRL_C delivery failed for PID={Pid}.", process.Id);
            }
        }

        // Phase 3: Escalate to force-kill entire process tree
        if (!SafeHasExited(process))
        {
            _logger.LogWarning("Process {Exe} (PID={Pid}) did not exit within {Timeout}s after graceful stop; force-killing tree.",
                Path.GetFileName(request.ExecutablePath), process.Id, timeout.TotalSeconds);
            TryKillTree(process);
            await WaitForExitWithTimeoutAsync(process, TimeSpan.FromSeconds(3));
            return (gracefulUsed, true);
        }

        return (gracefulUsed, false);
    }

    private static async Task<bool> WaitForExitWithTimeoutAsync(System.Diagnostics.Process process, TimeSpan timeout)
    {
        if (SafeHasExited(process))
            return true;

        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return SafeHasExited(process);
        }
    }

    private static bool SafeHasExited(System.Diagnostics.Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private void TryKillTree(System.Diagnostics.Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while force-killing process tree.");
        }
    }

    private static async Task ReadLinesAsync(StreamReader reader, Action<string>? callback, StringBuilder buffer)
    {
        const int maxBufferedChars = 256 * 1024;
        try
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                if (buffer.Length < maxBufferedChars)
                {
                    buffer.AppendLine(line);
                }

                callback?.Invoke(line);
            }
        }
        catch (IOException)
        {
            // Pipe closed when child exited.
        }
        catch (ObjectDisposedException)
        {
            // Stream disposed during teardown.
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _jobObject.Dispose();
    }
}
