using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

internal static class ChatCommandExecution
{
    public static async Task<string> RunAsync(string command, string directory, int timeoutSeconds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command) || command.Length > 8000) throw new ArgumentException("command は 1～8,000 文字で指定してください。");
        if (timeoutSeconds is < 1 or > 1800) throw new ArgumentException("timeout_seconds は 1～1,800 秒で指定してください。");
        var script = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; " +
            "$global:LASTEXITCODE = 0; try { & {\n" + command + "\n}; exit $LASTEXITCODE } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }";
        var info = new ProcessStartInfo(ProcessRunner.PowerShell) { WorkingDirectory = directory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-InputFormat", "Text", "-OutputFormat", "Text", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            info.ArgumentList.Add(arg);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        using var process = new Process { StartInfo = info };
        process.Start();
        using var stop = cancellation.Token.Register(() => {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { } // Retried and reported by the execution task.
        });
        var stdout = CaptureAsync(process.StandardOutput, cancellation.Token);
        var stderr = CaptureAsync(process.StandardError, cancellation.Token);
        try { await process.WaitForExitAsync(cancellation.Token); await Task.WhenAll(stdout, stderr); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Kill is synchronous; wait for process shutdown and both readers before disposing.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(stdout, stderr);
        }
        ct.ThrowIfCancellationRequested();
        var output = await stdout; var error = await stderr;
        return new JsonObject { ["working_directory"] = directory, ["exit_code"] = process.ExitCode,
            ["timed_out"] = timeout.IsCancellationRequested, ["stdout"] = output.Text, ["stderr"] = error.Text,
            ["stdout_truncated"] = output.Truncated, ["stderr_truncated"] = error.Truncated }.ToJsonString();
    }

    private static async Task<(string Text, bool Truncated)> CaptureAsync(StreamReader reader, CancellationToken ct)
    {
        const int limit = 20000;
        var text = new StringBuilder(); var buffer = new char[4096]; bool truncated = false;
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                int retained = Math.Min(count, limit - text.Length);
                text.Append(buffer, 0, retained); truncated |= retained < count;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        return (text.ToString(), truncated);
    }
}
