using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalLlm.Gui.Services;

public sealed class Settings
{
    [JsonIgnore]
    public string RepositoryRoot => ResolveRoot(AppContext.BaseDirectory);
    public const string DefaultModelPath = @"models\Qwen3.8-27B-Uncensored\Qwen3.8-27B-Uncensored-Q4_K_M.gguf";
    public const string DefaultMmprojPath = @"models\Qwen3.8-27B-Uncensored\mmproj-Qwen3.8-27B-Uncensored-F16.gguf";
    public const string DefaultServerExe = @"runtime\llama.cpp\llama-server.exe";
    public string ModelPath { get; set; } = DefaultModelPath;
    public string MmprojPath { get; set; } = DefaultMmprojPath;
    public string ServerExe { get; set; } = DefaultServerExe;
    public int ContextSize { get; set; } = 131072;
    public int Threads { get; set; } = 12;
    public int MtpDraftTokens { get; set; } = 2;
    public string CliWorkingDirectory { get; set; } = "";
    public int WebPort { get; set; } = 3000;
    public static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalLlmGui");
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
            if (File.Exists(Path.Combine(dir.FullName, "LocalLlm.Gui.csproj"))) return dir.FullName;
        // Published applications keep scripts and assets beside the executable.
        return Path.GetFullPath(start);
    }
}
