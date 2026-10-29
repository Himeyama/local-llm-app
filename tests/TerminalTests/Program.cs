using System.Text;
using System.Text.RegularExpressions;
using LocalLlm.Gui.Services;

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("ConPTY tests require Windows.");
var gate = new object();
var captured = new StringBuilder();
var decoder = Encoding.UTF8.GetDecoder();
PowerShellConsole? shell = null;
var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workspace"));
Directory.CreateDirectory(workspace);
shell = new PowerShellConsole(workspace, 100, 24, async bytes => {
    var chars = new char[Encoding.UTF8.GetMaxCharCount(bytes.Length)];
    var count = decoder.GetChars(bytes, chars, false);
    var text = new string(chars, 0, count);
    lock (gate) captured.Append(text);
    if (text.Contains("\x1b[6n") && shell != null) await shell.SendAsync("\x1b[1;1R");
});
try {
    await WaitFor("PS ", TimeSpan.FromSeconds(120));
    await shell.SendAsync("$terminalValue = 21; Write-Output ('CWD_' + (Get-Location).Path)\r");
    await WaitFor("CWD_" + workspace);
    await shell.SendAsync("Write-Output ('STATE_' + ($terminalValue * 2)); Write-Output ('日本語' + '確認')\r");
    await WaitFor("STATE_42"); await WaitFor("日本語確認");
    shell.Resize(120, 30);
    await shell.SendAsync("Write-Output ('SIZE_' + [Console]::WindowWidth + '_' + [Console]::WindowHeight)\r");
    await WaitFor("SIZE_120_30");
    await shell.SendAsync("$answer = Read-Host 'ANSWER_PROMPT'\r");
    await WaitFor("ANSWER_PROMPT:");
    await shell.SendAsync("interactive-input\r");
    await shell.SendAsync("Write-Output ('ANSWER_' + $answer)\r");
    await WaitFor("ANSWER_interactive-input");
    await shell.SendAsync("Write-Output ('SLEEP_' + 'START'); Start-Sleep 60\r");
    await WaitFor("SLEEP_START");
    await shell.SendAsync("\x03");
    await Task.Delay(300);
    await shell.SendAsync("Write-Output ('INTERRUPT_' + 'OK')\r");
    await WaitFor("INTERRUPT_OK", TimeSpan.FromSeconds(10));
    var exited = new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
    shell.Exited += code => exited.TrySetResult(code);
    await shell.SendAsync("exit 0\r");
    if (await exited.Task.WaitAsync(TimeSpan.FromSeconds(15)) != 0) throw new Exception("Unexpected exit code");
    Console.WriteLine("PASS: ConPTY workspace, persistent state, UTF-8, resize, Read-Host, Ctrl+C, exit");
}
finally { shell.Dispose(); }

async Task WaitFor(string expected, TimeSpan? timeout = null)
{
    var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
    while (DateTime.UtcNow < until) {
        lock (gate) {
            var plain = Regex.Replace(captured.ToString(), @"\x1b\[[0-?]*[ -/]*[@-~]", "");
            if (plain.Contains(expected, StringComparison.OrdinalIgnoreCase)) return;
        }
        await Task.Delay(50);
    }
    lock (gate) throw new Exception($"Missing {expected}\n{captured}");
}
