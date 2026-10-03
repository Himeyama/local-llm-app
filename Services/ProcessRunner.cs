using System.Diagnostics;
using System.Text;

namespace LocalLlm.Gui.Services;

public sealed class ProcessRunner : IDisposable
{
    private readonly Dictionary<string, Process> processes = new();
    private readonly object gate = new();
    public event Action<string>? Log;
    public event Action<string, int>? Exited;
    public bool IsRunning(string name)
    {
        lock (gate) return processes.TryGetValue(name, out var p) && !p.HasExited;
    }
    public void Start(string name, string exe, string workingDirectory, IEnumerable<string> arguments)
    {
        lock (gate)
        {
            if (processes.ContainsKey(name)) throw new InvalidOperationException($"{name} は実行中、または終了処理中です。");
            var info = new ProcessStartInfo(exe) { WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            foreach (var arg in arguments) info.ArgumentList.Add(arg);
            info.Environment["PYTHONUNBUFFERED"] = "1";
            var p = new Process { StartInfo = info };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke($"[{name}] {e.Data}"); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log?.Invoke($"[{name}] {e.Data}"); };
            try
            {
                p.Start();
                processes.Add(name, p);
                p.BeginOutputReadLine(); p.BeginErrorReadLine();
                Log?.Invoke($"[{name}] 起動 PID {p.Id}");
                _ = ObserveAsync(name, p);
            }
            catch { p.Dispose(); throw; }
        }
    }
    private async Task ObserveAsync(string name, Process p)
    {
        try { await p.WaitForExitAsync(); Log?.Invoke($"[{name}] 終了コード {p.ExitCode}"); Exited?.Invoke(name, p.ExitCode); }
        catch (Exception e) { Log?.Invoke($"[{name}] {e.Message}"); }
        finally { lock (gate) { processes.Remove(name); p.Dispose(); } }
    }
    public void Stop(string name)
    {
        lock (gate)
            if (processes.TryGetValue(name, out var p) && !p.HasExited)
            {
                p.Kill(entireProcessTree: true);
                Log?.Invoke($"[{name}] 子プロセスを含めて停止しました。");
            }
    }
    public void Dispose()
    {
        lock (gate)
            foreach (var name in processes.Keys.ToArray())
                try { Stop(name); } catch (Exception e) { Log?.Invoke($"[{name}] 停止失敗: {e.Message}"); }
    }
    public static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
    public static string[] ScriptArguments(string script, IReadOnlyDictionary<string, string?> args)
        => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", ScriptCommand(script, args) };

    public static string EncodedScript(string script, IReadOnlyDictionary<string, string?> args)
        => Convert.ToBase64String(Encoding.Unicode.GetBytes(ScriptCommand(script, args)));

    private static string ScriptCommand(string script, IReadOnlyDictionary<string, string?> args)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        foreach (var key in args.Keys)
            if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[A-Za-z][A-Za-z0-9]*$")) throw new ArgumentException("Invalid parameter name");
        return "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'; " +
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $OutputEncoding = [Console]::OutputEncoding; & " +
            Quote(script) + " " + string.Join(" ", args.Select(pair => "-" + pair.Key + (pair.Value == null ? "" : " " + Quote(pair.Value))));
    }
}
