using System.Text.Json;
using LocalLlm.Gui.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

var fresh = new Settings();
Check(fresh.ServerExe == @"runtime\native\local-rocm.exe", "New installs must use the Rust backend");
Check(fresh.MtpDraftTokens == 0, "MTP must default to disabled");
Check(!fresh.ServerLanAccess && fresh.ContextSize >= 65536, "Keep localhost and minimum context defaults");
var legacy = JsonSerializer.Deserialize<Settings>("{}")!;
Check(legacy.ServerExe == fresh.ServerExe, "Missing settings must receive the new backend default");
var custom = new Settings { ServerExe = @"C:\custom\llama-server.exe", ModelPath = @"D:\models\test.gguf" };
var restored = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(custom))!;
Check(restored.ServerExe == custom.ServerExe && restored.ModelPath == custom.ModelPath, "Explicit user paths must be preserved");

var temporary = Path.Combine(Path.GetTempPath(), "LocalLlmPackaging-" + Guid.NewGuid().ToString("N"));
try
{
    Directory.CreateDirectory(temporary);
    File.WriteAllText(Path.Combine(temporary, "LocalLlm.Gui.csproj"), "development parent");
    var app = Path.Combine(temporary, "installed", "app");
    Directory.CreateDirectory(app);
    File.WriteAllText(Path.Combine(app, "local-llm.install.json"), "{}");
    File.WriteAllText(Path.Combine(app, "Start-LlamaServer.ps1"), "# packaged launcher");
    Check(Settings.ResolveRoot(app) == app, "Installed app must not resolve to the development parent");
    var development = Path.Combine(temporary, "bin", "Debug");
    Directory.CreateDirectory(development);
    Check(Settings.ResolveRoot(development) == temporary, "Development builds must retain repository discovery");
}
finally
{
    var resolved = Path.GetFullPath(temporary);
    Check(Path.GetDirectoryName(resolved) == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))
        && Path.GetFileName(resolved).StartsWith("LocalLlmPackaging-", StringComparison.Ordinal), "Cleanup target escaped the test directory");
    if (Directory.Exists(resolved)) Directory.Delete(resolved, true);
}
Console.WriteLine("PASS: Rust defaults, explicit settings preserved, installed/development root resolution");
