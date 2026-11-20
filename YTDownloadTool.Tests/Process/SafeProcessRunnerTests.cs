using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using YTDownloadTool.Core.Process;

namespace YTDownloadTool.Tests.Process;

public sealed class SafeProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_CapturesStdOutAndStdErrWithoutDeadlock()
    {
        using var runner = new SafeProcessRunner(NullLogger<SafeProcessRunner>.Instance);

        var stdoutLines = new List<string>();
        var stderrLines = new List<string>();

        var result = await runner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", "Write-Output 'hello-out'; [Console]::Error.WriteLine('hello-err'); exit 0"],
            GracefulStop = GracefulStopStrategy.KillImmediate,
            OnStandardOutput = stdoutLines.Add,
            OnStandardError = stderrLines.Add
        });

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.WasCancelled);
        Assert.Contains("hello-out", stdoutLines);
        Assert.Contains("hello-err", stderrLines);
    }

    [Fact]
    public async Task RunAsync_ArgumentListPreventsShellInjection()
    {
        using var runner = new SafeProcessRunner(NullLogger<SafeProcessRunner>.Instance);

        // If shell interpolation occurred, "& echo INJECTED" would execute as a second command.
        const string maliciousArg = "safe & echo INJECTED";
        var result = await runner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = "cmd.exe",
            Arguments = ["/c", "echo", maliciousArg],
            GracefulStop = GracefulStopStrategy.KillImmediate
        });

        Assert.Equal(0, result.ExitCode);
        // cmd /c echo receives the single quoted argument verbatim without splitting on '&'
        Assert.DoesNotContain("\nINJECTED", result.StandardOutput);
    }

    [Fact]
    public async Task RunAsync_StdinQuitStrategy_StopsProcessGracefully()
    {
        using var runner = new SafeProcessRunner(NullLogger<SafeProcessRunner>.Instance);
        using var cts = new CancellationTokenSource();

        // Simulate FFmpeg waiting for 'q' on stdin and exiting cleanly with exit code 0 when it receives 'q'
        const string script = """
            Write-Output 'READY'
            while ($true) {
                $line = [Console]::In.ReadLine()
                if ($line -eq 'q') {
                    Write-Output 'FINALIZED_OK'
                    exit 0
                }
            }
            """;

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawFinalized = false;

        var runTask = runner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
            GracefulStop = GracefulStopStrategy.StdinQuitThenCtrlC,
            GracefulStopTimeout = TimeSpan.FromSeconds(6),
            OnStandardOutput = line =>
            {
                if (line.Contains("READY")) readyTcs.TrySetResult();
                if (line.Contains("FINALIZED_OK")) sawFinalized = true;
            }
        }, cts.Token);

        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();

        var result = await runTask;

        Assert.True(result.WasCancelled);
        Assert.True(result.GracefulStopUsed);
        Assert.False(result.ForceKilled);
        Assert.True(sawFinalized);
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CtrlCStrategy_DeliversRealCtrlCEventOnWindows()
    {
        using var runner = new SafeProcessRunner(NullLogger<SafeProcessRunner>.Instance);
        using var cts = new CancellationTokenSource();

        // Real Windows console child (C# Console.CancelKeyPress inside PowerShell) that traps CTRL_C_EVENT
        // and writes a marker + exits cleanly with code 42 instead of being abruptly killed.
        const string script = """
            $code = @"
            using System;
            using System.Threading;
            public static class CtrlCTrap {
                public static int WaitForCtrlC() {
                    var ev = new ManualResetEventSlim(false);
                    Console.CancelKeyPress += (s, e) => {
                        e.Cancel = true;
                        Console.WriteLine("TRAPPED_CTRL_C");
                        Console.Out.Flush();
                        ev.Set();
                    };
                    Console.WriteLine("TRAP_READY");
                    Console.Out.Flush();
                    return ev.Wait(TimeSpan.FromSeconds(20)) ? 42 : 99;
                }
            }
            "@
            Add-Type -TypeDefinition $code
            exit ([CtrlCTrap]::WaitForCtrlC())
            """;

        var readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var trapped = false;

        var runTask = runner.RunAsync(new ProcessRunRequest
        {
            ExecutablePath = "powershell.exe",
            Arguments = ["-NoProfile", "-NonInteractive", "-Command", script],
            GracefulStop = GracefulStopStrategy.CtrlC,
            GracefulStopTimeout = TimeSpan.FromSeconds(10),
            OnStandardOutput = line =>
            {
                if (line.Contains("TRAP_READY")) readyTcs.TrySetResult();
                if (line.Contains("TRAPPED_CTRL_C")) trapped = true;
            }
        }, cts.Token);

        await readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        cts.Cancel();

        var result = await runTask;

        Assert.True(result.WasCancelled);
        Assert.True(result.GracefulStopUsed);
        Assert.False(result.ForceKilled);
        Assert.True(trapped, $"Expected child to trap CTRL_C_EVENT. stdout='{result.StandardOutput}' stderr='{result.StandardError}'");
        Assert.Equal(42, result.ExitCode);
    }

    [Fact]
    public async Task WindowsJobObject_DisposingJobTerminatesAssignedChildProcess()
    {
        int childPid;
        using (var job = new WindowsJobObject())
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add("Start-Sleep -Seconds 60");

            using var proc = System.Diagnostics.Process.Start(psi)!;
            childPid = proc.Id;
            Assert.True(job.TryAssignProcess(proc));
            Assert.False(proc.HasExited);
        }

        // Wait briefly for OS kernel to terminate the job's processes upon handle close
        var exited = false;
        for (var i = 0; i < 30; i++)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(childPid);
                if (p.HasExited)
                {
                    exited = true;
                    break;
                }
            }
            catch (ArgumentException)
            {
                exited = true;
                break;
            }

            await Task.Delay(100);
        }

        Assert.True(exited, $"Child process {childPid} was not terminated when WindowsJobObject was disposed.");
    }
}
