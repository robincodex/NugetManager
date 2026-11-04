using NugetManager.Presentation;
using static NugetManager.Presentation.Messages;
using System.Diagnostics;
using System.Text;

namespace NugetManager.Sdk;

internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string?>? Environment = null);

internal sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

internal sealed class ProcessTimeoutException(UiMessage message) : Exception(message.ToString()), IUiException
{
    public UiMessage UiMessage { get; } = message;
}

internal sealed class ProcessStartException(UiMessage message, Exception inner) : Exception(message.ToString(), inner), IUiException
{
    public UiMessage UiMessage { get; } = message;
}

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct);
}

internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Id of the most recently started child; used by tests to verify termination.</summary>
    public int? LastProcessId { get; private set; }

    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(request.FileName)
        {
            WorkingDirectory = request.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in request.Arguments)
        {
            psi.ArgumentList.Add(arg);
        }
        psi.Environment["DOTNET_CLI_FORCE_UTF8_ENCODING"] = "true";
        psi.Environment["DOTNET_NOLOGO"] = "1";
        psi.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new ProcessStartException(MF(TextKey.M221, $"无法启动 {request.FileName}：{UiMessage.FromException(ex)}"), ex);
        }
        LastProcessId = process.Id;
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await DrainAsync(stdoutTask, stderrTask).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                throw UiErrors.OperationCanceled(M(TextKey.M222), ct);
            }
            throw new ProcessTimeoutException(
                MF(TextKey.M223, $"{request.FileName} {Describe(request.Arguments)} 超过 {request.Timeout.TotalSeconds:0} 秒未完成，已终止。"));
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    public static string Describe(IReadOnlyList<string> arguments) =>
        string.Join(' ', arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static async Task DrainAsync(Task<string> a, Task<string> b)
    {
        try
        {
            await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or ObjectDisposedException)
        {
        }
    }
}
