using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新しいチャット";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string Workspace { get; set; } = "";
    public string WorkspaceId { get; set; } = "";
    public bool ConsoleOpen { get; set; }
    public List<JsonObject> Messages { get; set; } = new();
    public int? ContextTokens { get; set; }
    public string Summary { get; set; } = "";
    public override string ToString() => Title;
}

public sealed class ChatStore(string directory)
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool> consoleVisibility = new();

    public List<ChatSession> Load()
    {
        lock (gate) {
        Directory.CreateDirectory(directory);
        var sessions = new List<ChatSession>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            // A damaged history must not hide the remaining sessions.
            try { var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file)); if (session != null) { sessions.Add(session); consoleVisibility[session.Id] = session.ConsoleOpen; } }
            catch (JsonException) { }
        }
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        }
    }

    public void Save(ChatSession session, bool updateTimestamp = true)
    {
        lock (gate) {
            var path = SessionPath(session);
            if (!consoleVisibility.TryGetValue(session.Id, out var visible)) {
                visible = File.Exists(path) ? JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path))?.ConsoleOpen ?? session.ConsoleOpen : session.ConsoleOpen;
                consoleVisibility[session.Id] = visible;
            }
            // A background generation snapshot must not overwrite a newer UI preference.
            session.ConsoleOpen = visible;
            Write(session, updateTimestamp);
        }
    }

    public void SaveConsoleVisibility(ChatSession session, bool visible)
    {
        lock (gate) {
            var path = SessionPath(session);
            // Preserve the latest transcript even when the UI's last snapshot is behind generation.
            var stored = File.Exists(path) ? JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(path)) ?? session : session;
            stored.ConsoleOpen = visible;
            Write(stored, updateTimestamp: false);
            consoleVisibility[session.Id] = visible;
            session.ConsoleOpen = visible;
        }
    }

    private string SessionPath(ChatSession session)
    {
        if (!Guid.TryParseExact(session.Id, "N", out _)) throw new InvalidDataException("チャット ID が不正です。");
        return Path.Combine(directory, session.Id + ".json");
    }

    private void Write(ChatSession session, bool updateTimestamp)
    {
        Directory.CreateDirectory(directory);
        if (updateTimestamp) session.UpdatedAt = DateTime.Now;
        var path = SessionPath(session);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(session));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete(ChatSession session)
    {
        lock (gate) { File.Delete(SessionPath(session)); consoleVisibility.Remove(session.Id); }
    }
}

public sealed class ChatWorkspace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Directory { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsDefault { get; set; }
    public bool Expanded { get; set; } = true;
    public string Name => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName : Path.GetFileName(Directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) is { Length: > 0 } name ? name : Directory;
    public override string ToString() => Name;

    public static ChatWorkspace EnsureDefault(List<ChatWorkspace> workspaces, string documentsDirectory)
    {
        var existing = workspaces.FirstOrDefault(w => w.IsDefault);
        if (existing != null) return existing;
        if (string.IsNullOrWhiteSpace(documentsDirectory)) throw new DirectoryNotFoundException("Documents フォルダーの場所を取得できません。");
        var workspace = new ChatWorkspace { Directory = documentsDirectory, DisplayName = "デフォルト", IsDefault = true };
        workspaces.Add(workspace);
        return workspace;
    }

    public static List<ChatSession> Reconcile(List<ChatWorkspace> workspaces, IEnumerable<ChatSession> sessions, string defaultDirectory)
    {
        var changed = new List<ChatSession>();
        foreach (var session in sessions) {
            var workspace = workspaces.FirstOrDefault(w => w.Id == session.WorkspaceId);
            if (workspace == null) {
                var path = string.IsNullOrWhiteSpace(session.Workspace) ? defaultDirectory : session.Workspace;
                workspace = workspaces.FirstOrDefault(w => string.Equals(w.Directory.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
                if (workspace == null) { workspace = new ChatWorkspace { Directory = path }; workspaces.Add(workspace); }
            }
            if (session.WorkspaceId == workspace.Id && session.Workspace == workspace.Directory) continue;
            session.WorkspaceId = workspace.Id;
            session.Workspace = workspace.Directory;
            changed.Add(session);
        }
        return changed;
    }
}
