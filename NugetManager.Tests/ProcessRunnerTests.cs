using System.Diagnostics;
using NugetManager.Sdk;

namespace NugetManager.Tests;

public class ProcessRunnerTests
{
    private static ProcessRequest Pwsh(string script, TimeSpan? timeout = null, params string[] extra)
    {
        var args = new List<string> { "-NoProfile", "-NonInteractive", "-Command", script };
        args.AddRange(extra);
        return new ProcessRequest("powershell.exe", args, Path.GetTempPath(), timeout ?? TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task ProcessArgumentsPreserveSpaces()
    {
        var request = new ProcessRequest("cmd.exe", ["/c", "echo", "argument with spaces"], Path.GetTempPath(), TimeSpan.FromSeconds(30));
        var result = await new ProcessRunner().RunAsync(request, CancellationToken.None);
        // cmd echoes the quoted argument verbatim, proving it arrived as a single argument.
        Assert.Equal("\"argument with spaces\"", result.Stdout.Trim());
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task BothOutputStreamsDrain()
    {
        var script = "$o=[Console]::Out; $e=[Console]::Error; for($i=0;$i -lt 20000;$i++){ $o.WriteLine('out' + $i); $e.WriteLine('err' + $i) }; exit 3";
        var result = await new ProcessRunner().RunAsync(Pwsh(script), CancellationToken.None);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains("out19999", result.Stdout);
        Assert.Contains("err19999", result.Stderr);
    }

    [Fact]
    public async Task CancellationStopsOwnedChild()
    {
        using var cts = new CancellationTokenSource();
        var runner = new ProcessRunner();
        var task = runner.RunAsync(Pwsh("[Console]::Out.WriteLine($PID); [Console]::Out.Flush(); Start-Sleep -Seconds 60"), cts.Token);
        await Task.Delay(1500);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.NotNull(runner.LastProcessId);
        await AssertExitedAsync(runner.LastProcessId!.Value);
    }

    [Fact]
    public async Task TimeoutReportsFailure()
    {
        var runner = new ProcessRunner();
        var error = await Assert.ThrowsAsync<ProcessTimeoutException>(
            () => runner.RunAsync(Pwsh("Start-Sleep -Seconds 60", TimeSpan.FromSeconds(2)), CancellationToken.None));
        Assert.Contains("powershell.exe", error.Message);
        await AssertExitedAsync(runner.LastProcessId!.Value);
    }

    private static async Task AssertExitedAsync(int pid)
    {
        for (var i = 0; i < 50; i++)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited)
                {
                    return;
                }
            }
            catch (ArgumentException)
            {
                return;
            }
            await Task.Delay(100);
        }
        Assert.Fail($"Process {pid} still running.");
    }
}
