using System.Collections.Concurrent;
using System.Diagnostics;
using LocalLlm.Gui.Services;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Wait(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (!condition()) await Task.Delay(50, timeout.Token);
}

foreach (var scale in new[] { 1.0, 1.25, 1.5, 2.0, 3.0 })
{
    var spacious = WindowSizing.InitialClientSize(scale, 7680, 4320);
    Assert(spacious == ((int)Math.Round(1380 * scale), (int)Math.Round(900 * scale)), "Initial size must scale in DIPs");
    var constrained = WindowSizing.InitialClientSize(scale, 1920, 1080);
    Assert(constrained.Width <= 1728 && constrained.Height <= 972, "High-DPI window must fit working area");
}
Assert(WindowSizing.InitialClientSize(2, 1280, 720) == (1152, 648), "Small monitor should not enforce an oversized minimum");
Console.WriteLine("PASS: 100/125/150/200/300% DPI sizing and small-display working area limits");

var startup = new ServerStartupStatus();
startup.Begin();
var firstFrame = startup.Spinner;
startup.Tick();
Assert(startup.IsStarting && startup.Spinner != firstFrame, "Startup spinner must animate");
startup.ObserveLog("llama_model_loader: loaded meta data");
startup.ObserveLog("load_tensors: loading model tensors");
Assert(startup.Message.Contains("読み込"), "Model loading phase missing");
startup.ObserveLog("load_tensors: offloading 64 layers to GPU");
Assert(startup.Message.Contains("GPU"), "GPU phase missing");
startup.ObserveLog("llama_context: constructing llama_context");
startup.ObserveLog("common_init_from_params: warming up the model with an empty run");
Assert(startup.Message.Contains("ウォームアップ"), "Warmup phase missing");
startup.ObserveLog("load_tensors: delayed worker log");
startup.SetConnection(false);
Assert(startup.IsStarting && startup.Message.Contains("ウォームアップ"), "Loading status must survive delayed logs and unavailable health probes");
startup.ObserveLog("server is listening on http://127.0.0.1:9931");
Assert(startup.IsStarting, "Listening log alone must not imply API readiness");
startup.SetConnection(true, "test-model");
Assert(!startup.IsStarting && startup.Spinner == "" && startup.Message.Contains("test-model"), "Successful probe must stop spinner and show model");
startup.Begin(); startup.Finish(1);
Assert(!startup.IsStarting && startup.Spinner == "" && startup.Message.Contains("1"), "Failed process must stop spinner");
startup.Begin(); startup.Failed();
Assert(!startup.IsStarting && startup.Message.Contains("失敗"), "Launch exception must stop spinner");
startup.Begin(); startup.Finish(0);
Assert(!startup.IsStarting && startup.Message.Contains("停止"), "Stopped process must stop spinner");
startup.Begin(); startup.Finish(-1, stopRequested: true);
Assert(!startup.IsStarting && !startup.IsConnected && startup.Spinner == "" && startup.Message == "未接続", "Requested server stop must not show an exit error");
Console.WriteLine("PASS: startup spinner, loading phases, delayed logs, readiness, failure and stop");

var webStartup = new WebStartupStatus();
webStartup.Begin();
var webFirstFrame = webStartup.Spinner;
webStartup.Tick();
Assert(webStartup.IsStarting && webStartup.Spinner != webFirstFrame, "Web startup spinner must animate");
webStartup.ObserveLog("Loading WEBUI_SECRET_KEY from .webui_secret_key");
Assert(webStartup.Message.Contains("設定"), "Web configuration phase missing");
webStartup.ObserveLog("alembic.runtime.migration: Running upgrade");
Assert(webStartup.Message.Contains("データベース"), "Web database phase missing");
webStartup.ObserveLog("Embedding model set: local-model");
Assert(webStartup.Message.Contains("検索用モデル"), "Web model preparation phase missing");
webStartup.ObserveLog("INFO: Waiting for application startup.");
Assert(webStartup.Message.Contains("初期化"), "Web application startup phase missing");
webStartup.ObserveLog("alembic: delayed log");
webStartup.SetConnection(false);
Assert(webStartup.IsStarting && webStartup.Message.Contains("初期化"), "Web startup must survive unavailable health and delayed logs");
webStartup.ObserveLog("INFO: Application startup complete.");
Assert(webStartup.IsStarting && webStartup.Message.Contains("接続"), "Web readiness requires a successful health check");
webStartup.SetConnection(true);
Assert(!webStartup.IsStarting && webStartup.IsConnected && webStartup.Spinner == "", "Healthy WebUI must stop spinner");
webStartup.SetConnection(false);
Assert(!webStartup.IsConnected && webStartup.Message == "未接続", "Web disconnect must update status");
webStartup.Begin(); webStartup.Finish(1);
Assert(!webStartup.IsStarting && webStartup.Message.Contains("1"), "Web exit failure must stop spinner");
webStartup.Begin(); webStartup.Failed();
Assert(!webStartup.IsStarting && webStartup.Message.Contains("失敗"), "Web launch exception must stop spinner");
webStartup.Begin(); webStartup.Finish(0);
Assert(!webStartup.IsStarting && webStartup.Message.Contains("停止"), "Web stop must stop spinner");
webStartup.Begin(); webStartup.Finish(-1, stopRequested: true);
Assert(!webStartup.IsStarting && !webStartup.IsConnected && webStartup.Message == "未接続", "Requested WebUI stop must not show an exit error");
Console.WriteLine("PASS: WebUI spinner, startup phases, readiness, disconnect, failure and stop");

var root = Path.Combine(Path.GetTempPath(), "LocalLlmGui-tests-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    var script = Path.Combine(root, "引数 ' space.ps1");
    var result = Path.Combine(root, "result.txt");
    var tricky = "空白 ' quote ` $HOME ; $(Get-Process)";
    File.WriteAllText(script, "param([int]$ContextSize,[string]$ModelPath,[switch]$Capture)\n[IO.File]::WriteAllText('" + result.Replace("'", "''") + "', \"$ContextSize|$ModelPath|$Capture\")");
    using var runner = new ProcessRunner();
    var logs = new ConcurrentQueue<string>(); runner.Log += logs.Enqueue;
    var exits = new ConcurrentQueue<(string Name, int Code, bool StopRequested)>();
    runner.Exited += (name, code, stopRequested) => exits.Enqueue((name, code, stopRequested));
    runner.Start("args", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(script, new Dictionary<string, string?> { ["ContextSize"] = "131072", ["ModelPath"] = tricky, ["Capture"] = null }));
    await Wait(() => logs.Any(s => s.Contains("[args] 終了コード")));
    Assert(File.ReadAllText(result) == "131072|" + tricky + "|True", "PowerShell named parameter / quoting regression");
    Assert(logs.Any(s => s.Contains("終了コード 0")), "Script should succeed");
    Console.WriteLine("PASS: PowerShell named parameters, Unicode, spaces, quotes, literal metacharacters");

    logs.Clear();
    var japaneseScript = Path.Combine(root, "日本語ログ.ps1");
    File.WriteAllText(japaneseScript, "Write-Progress -Activity 'モデル読み込み' -Status '準備中'\nWrite-Host '日本語の起動メッセージ'\n[Console]::Error.WriteLine('日本語のエラー')\nWrite-Error '起動できません'", new System.Text.UTF8Encoding(true));
    runner.Start("japanese", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(japaneseScript, new Dictionary<string, string?>()));
    await Wait(() => logs.Any(s => s.Contains("[japanese] 終了コード")));
    Assert(logs.Any(s => s.Contains("日本語の起動メッセージ")) && logs.Any(s => s.Contains("日本語のエラー")) && logs.Any(s => s.Contains("起動できません")), "Japanese stdout/stderr must remain readable");
    Assert(!logs.Any(s => s.Contains("CLIXML") || s.Contains("<Objs") || s.Contains("_x000D_") || s.Contains("モデル読み込み")), "Background PowerShell must emit plain text without progress metadata");
    Assert(logs.Any(s => s.Contains("[japanese] 終了コード 1")), "PowerShell failure exit code must be preserved");
    Console.WriteLine("PASS: Japanese host/error output, plain text, progress suppression and failure exit code");

    logs.Clear();
    var repository = new DirectoryInfo(AppContext.BaseDirectory);
    while (repository != null && !File.Exists(Path.Combine(repository.FullName, "LocalLlm.Gui.csproj"))) repository = repository.Parent;
    Assert(repository != null, "Server launcher source missing");
    Assert(Settings.ResolveRoot(repository!.FullName) == repository.FullName, "GUI root must resolve locally");
    var isolated = Path.Combine(root, "standalone");
    Directory.CreateDirectory(isolated);
    File.WriteAllText(Path.Combine(root, "Start-LlamaServer.ps1"), "parent trap");
    Assert(Settings.ResolveRoot(isolated) == isolated, "Published GUI must not search parent launchers");
    var oldSettings = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"RepositoryRoot\":\"C:/external\",\"Threads\":8}")!;
    Assert(oldSettings.RepositoryRoot != "C:/external" && oldSettings.Threads == 8, "Legacy external root must be ignored without losing preferences");
    Assert(!oldSettings.ServerLanAccess, "Existing settings must default to loopback access");
    var lanSettings = System.Text.Json.JsonSerializer.Deserialize<Settings>(System.Text.Json.JsonSerializer.Serialize(new Settings { ServerLanAccess = true }))!;
    Assert(lanSettings.ServerLanAccess, "LAN access preference must survive serialization");
    Console.WriteLine("PASS: independent root resolution and legacy settings migration");
    var serverLauncher = Path.Combine(root, "Start-LlamaServer.ps1");
    File.Copy(Path.Combine(repository!.FullName, "Start-LlamaServer.ps1"), serverLauncher, true);
    File.WriteAllText(Path.Combine(root, "model.gguf"), "");
    File.WriteAllText(Path.Combine(root, "mmproj.gguf"), "");
    var fakeServer = Path.Combine(root, "fake server.cmd");
    File.WriteAllText(fakeServer, "@echo off\r\necho MOCK-SERVER-OK %*\r\nexit /b 0\r\n", System.Text.Encoding.ASCII);
    var harness = Path.Combine(root, "server-harness.ps1");
    File.WriteAllText(harness, "function Get-Command { throw 'External executable lookup is forbidden' }\nfunction Get-NetTCPConnection {}\n& $PSScriptRoot\\Start-LlamaServer.ps1 -ModelPath model.gguf -MmprojPath mmproj.gguf -ServerExe 'fake server.cmd'", new System.Text.UTF8Encoding(true));
    runner.Start("launcher", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(harness, new Dictionary<string, string?>()));
    await Wait(() => logs.Any(s => s.Contains("[launcher] 終了コード")));
    Assert(logs.Any(s => s.Contains("ROCm llama-server を起動します")), "Windows PowerShell must decode the real launcher as UTF-8");
    Assert(logs.Any(s => s.Contains("MOCK-SERVER-OK") && s.Contains("--host 127.0.0.1") && s.Contains("--alias model") && s.Contains("-c 131072")), "Default executable resolution and model-relative arguments must reach the mock server");
    Assert(logs.Any(s => s.Contains("[launcher] 終了コード 0")), "Real launcher must succeed with a mock executable");
    Console.WriteLine("PASS: real server launcher in Windows PowerShell with mocked executable; no model loaded");
    logs.Clear();
    var lanHarness = Path.Combine(root, "lan-harness.ps1");
    File.WriteAllText(lanHarness, File.ReadAllText(harness) + " -LanAccess:$LanAccess", new System.Text.UTF8Encoding(true));
    // Pass the switch through the same argument builder used by the GUI.
    File.WriteAllText(lanHarness, "param([switch]$LanAccess)\n" + File.ReadAllText(lanHarness), new System.Text.UTF8Encoding(true));
    runner.Start("lan-launcher", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(lanHarness, new Dictionary<string, string?> { ["LanAccess"] = null }));
    await Wait(() => logs.Any(s => s.Contains("[lan-launcher] 終了コード")));
    Assert(logs.Any(s => s.Contains("MOCK-SERVER-OK") && s.Contains("--host 0.0.0.0") && s.Contains("--port 9931")), "LAN switch must bind to all IPv4 interfaces on the existing port");
    Assert(logs.Any(s => s.Contains("[lan-launcher] 終了コード 0")), "LAN launcher must succeed with a mock executable");
    Console.WriteLine("PASS: LAN launcher switch and persisted preference; no model loaded");
    logs.Clear();
    var absoluteHarness = File.ReadAllText(harness).Replace("-ModelPath model.gguf -MmprojPath mmproj.gguf -ServerExe 'fake server.cmd'",
        "-ModelPath (Join-Path $PSScriptRoot 'model.gguf') -MmprojPath (Join-Path $PSScriptRoot 'mmproj.gguf') -ServerExe (Join-Path $PSScriptRoot 'fake server.cmd')");
    File.WriteAllText(harness, absoluteHarness, new System.Text.UTF8Encoding(true));
    runner.Start("absolute-launcher", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(harness, new Dictionary<string, string?>()));
    await Wait(() => logs.Any(s => s.Contains("[absolute-launcher] 終了コード")));
    Assert(logs.Any(s => s.Contains("MOCK-SERVER-OK") && s.Contains(Path.Combine(root, "model.gguf"))) && logs.Any(s => s.Contains("[absolute-launcher] 終了コード 0")), "Absolute asset paths must reach the server without joining the repository prefix");
    Console.WriteLine("PASS: absolute model, mmproj and executable paths; no model loaded");


    logs.Clear();
    runner.Start("exit", ProcessRunner.PowerShell, root, new[] { "-NoProfile", "-Command", "[Console]::Error.WriteLine('intentional error'); exit 7" });
    await Wait(() => logs.Any(s => s.Contains("[exit] 終了コード")));
    Assert(logs.Any(s => s.Contains("intentional error")) && logs.Any(s => s.Contains("終了コード 7")), "stderr / nonzero exit missing");
    await Wait(() => exits.Any(e => e.Name == "exit"));
    Assert(exits.Any(e => e.Name == "exit" && e.Code == 7 && !e.StopRequested), "Unexpected failure must remain distinguishable from a requested stop");
    Console.WriteLine("PASS: stderr and nonzero exit reporting");

    var childPid = Path.Combine(root, "child.txt");
    var treeScript = Path.Combine(root, "tree.ps1");
    File.WriteAllText(treeScript, "$p = Start-Process -FilePath '" + ProcessRunner.PowerShell.Replace("'", "''") + "' -ArgumentList '-NoProfile -Command Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru; [IO.File]::WriteAllText('" + childPid.Replace("'", "''") + "', [string]$p.Id); Start-Sleep -Seconds 60");
    runner.Start("tree", ProcessRunner.PowerShell, root, ProcessRunner.ScriptArguments(treeScript, new Dictionary<string, string?>()));
    await Wait(() => File.Exists(childPid) && File.ReadAllText(childPid).Length > 0);
    var child = Process.GetProcessById(int.Parse(File.ReadAllText(childPid)));
    var rejected = false;
    try { runner.Start("tree", ProcessRunner.PowerShell, root, Array.Empty<string>()); } catch (InvalidOperationException) { rejected = true; }
    Assert(rejected, "Duplicate start must be rejected");
    runner.Stop("tree");
    await Wait(() => child.HasExited);
    await Wait(() => !runner.IsRunning("tree"));
    await Wait(() => exits.Any(e => e.Name == "tree"));
    Assert(exits.Any(e => e.Name == "tree" && e.StopRequested), "Process tree kill must be reported as a requested stop");
    child.Dispose();
    Console.WriteLine("PASS: duplicate prevention and owned process tree shutdown");
}
finally
{
    // Only the unique temporary directory created by this test is removed.
    Directory.Delete(root, recursive: true);
}
