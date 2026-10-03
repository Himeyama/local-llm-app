using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = "新しいチャット";
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string Workspace { get; set; } = "";
    public List<JsonObject> Messages { get; set; } = new();
    public override string ToString() => Title;
}

public sealed class ChatStore(string directory)
{
    public List<ChatSession> Load()
    {
        Directory.CreateDirectory(directory);
        var sessions = new List<ChatSession>();
        foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
        {
            // A damaged history must not hide the remaining sessions.
            try { var session = JsonSerializer.Deserialize<ChatSession>(File.ReadAllText(file)); if (session != null) sessions.Add(session); }
            catch (JsonException) { }
        }
        return sessions.OrderByDescending(s => s.UpdatedAt).ToList();
    }

    public void Save(ChatSession session)
    {
        if (!Guid.TryParseExact(session.Id, "N", out _)) throw new InvalidDataException("チャット ID が不正です。");
        Directory.CreateDirectory(directory);
        session.UpdatedAt = DateTime.Now;
        var path = Path.Combine(directory, session.Id + ".json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(session));
        File.Move(temporary, path, overwrite: true);
    }

    public void Delete(ChatSession session)
    {
        if (!Guid.TryParseExact(session.Id, "N", out _)) throw new InvalidDataException("チャット ID が不正です。");
        File.Delete(Path.Combine(directory, session.Id + ".json"));
    }
}
