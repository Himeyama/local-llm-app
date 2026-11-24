using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalLlm.Gui.Services;

public sealed class Settings
{
    [JsonIgnore]
    public string RepositoryRoot => ResolveRoot(AppContext.BaseDirectory);
    [JsonIgnore]
    public string DefaultWorkspaceDirectory => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    public const string DefaultModelPath = @"models\Qwen3.8-27B-Uncensored\Qwen3.8-27B-Uncensored-Q4_K_M.gguf";
    public const string DefaultMmprojPath = @"models\Qwen3.8-27B-Uncensored\mmproj-Qwen3.8-27B-Uncensored-F16.gguf";
    public const string DefaultServerExe = @"runtime\native\local-rocm.exe";
    public string ModelPath { get; set; } = DefaultModelPath;
    public string MmprojPath { get; set; } = DefaultMmprojPath;
    public string ServerExe { get; set; } = DefaultServerExe;
    public int ContextSize { get; set; } = 131072;
    public int Threads { get; set; } = 12;
    public int MtpDraftTokens { get; set; } = 0;
    public bool ServerLanAccess { get; set; } = false;
    public string CliWorkingDirectory { get; set; } = "";
    public bool ChatWebEnabled { get; set; } = true;
    public string ChatProvider { get; set; } = "local";
    public string GeminiUrl { get; set; } = "http://localhost:8765/ai-foundation/chat-ai/gemini/gemini-3.5-flash:streamGenerateContentSse";
    public string GeminiAuthMethod { get; set; } = "none";
    public string GeminiApiKey { get; set; } = "";
    public bool ChatWritesEnabled { get; set; } = true;
    public bool ChatCommandsEnabled { get; set; } = true;
    public string ChatReasoningEffort { get; set; } = "xhigh";
    public bool ChatShowReasoning { get; set; } = true;
    public bool InvestigationShowReasoning { get; set; } = false;
    public bool EditorAutoSave { get; set; } = true;
    public bool EditorWordWrap { get; set; } = true;
    private double appScale = 1.0;
    public double AppScale
    {
        get => appScale;
        set => appScale = double.IsFinite(value) ? Math.Clamp(value, 0.75, 1.5) : 1.0;
    }
    public bool? ConsoleExpanded { get; set; }
    public double ConsoleHeight { get; set; } = 280;
    public double EditorWidthRatio { get; set; } = 0.5;
    public double ExplorerPaneHeight { get; set; } = 240;
    public double GitPaneHeight { get; set; } = 320;
    public bool ExplorerSectionExpanded { get; set; } = true;
    public bool ChatSectionExpanded { get; set; } = true;
    public bool GitSectionExpanded { get; set; } = true;
    public bool DefaultWorkspaceInitialized { get; set; }
    public List<ChatWorkspace> ChatWorkspaces { get; set; } = new();
    public string LastChatId { get; set; } = "";
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLlmGui");
    public static string NodeExecutable
    {
        get
        {
            var bundled = Path.Combine(ResolveRoot(AppContext.BaseDirectory), "runtime", "node", "node.exe");
            return File.Exists(bundled) ? bundled : "node";
        }
    }
    public void EnsureDefaultWorkspace()
    {
        if (DefaultWorkspaceInitialized) return;
        ChatWorkspace.EnsureDefault(ChatWorkspaces, DefaultWorkspaceDirectory);
        DefaultWorkspaceInitialized = true;
    }

    public static Settings Load()
    {
        var path = Path.Combine(DataDirectory, "settings.json");
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new();
    }
    public void Save()
    {
        Directory.CreateDirectory(DataDirectory);
        File.WriteAllText(Path.Combine(DataDirectory, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static string ResolveRoot(string start)
    {
        // Development builds use this GUI project, never a parent backend repo.
        for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "local-llm.install.json"))
                && File.Exists(Path.Combine(dir.FullName, "Start-LlamaServer.ps1"))) return dir.FullName;
            if (File.Exists(Path.Combine(dir.FullName, "LocalLlm.Gui.csproj"))) return dir.FullName;
        }
        // Published applications keep scripts and assets beside the executable.
        return Path.GetFullPath(start);
    }
}
