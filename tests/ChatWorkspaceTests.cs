using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLlm.Gui.Services;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
var workspaces = new List<ChatWorkspace>();
var first = new ChatSession { Workspace = @"C:\work\demo", Title = "既存の会話", UpdatedAt = new DateTime(2020, 1, 1), Messages = new() { new JsonObject { ["role"] = "user", ["content"] = "保持する履歴" } } };
var second = new ChatSession { Workspace = @"c:\work\DEMO\" };
var third = new ChatSession { Workspace = @"C:\work\other" };
var sessions = new[] { first, second, third };
var changed = ChatWorkspace.Reconcile(workspaces, sessions, @"C:\default");
Assert(workspaces.Count == 2 && changed.Count == 3, "Legacy sessions must migrate into directory groups");
Assert(first.WorkspaceId == second.WorkspaceId && first.WorkspaceId != third.WorkspaceId, "Workspace membership must match directory groups");
Assert(first.Title == "既存の会話" && first.UpdatedAt.Year == 2020 && first.Messages[0]["content"]!.GetValue<string>() == "保持する履歴", "Migration must preserve transcripts and timestamps");
Assert(ChatWorkspace.Reconcile(workspaces, sessions, @"C:\default").Count == 0, "Migration must be idempotent");
workspaces[0].Directory = @"C:\moved";
Assert(ChatWorkspace.Reconcile(workspaces, sessions, @"C:\default").Count == 2 && first.Workspace == @"C:\moved" && second.Workspace == first.Workspace, "Workspace directory must apply to all member sessions");
workspaces[0].Expanded = false;
var settings = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(new Settings { ChatWorkspaces = workspaces }))!;
Assert(settings.ChatWorkspaces.Count == 2 && !settings.ChatWorkspaces[0].Expanded && settings.ChatWorkspaces[0].Id == first.WorkspaceId, "Workspace identity, directory and expansion must persist");
var emptyWorkspace = new ChatWorkspace { Directory = @"C:\empty" };
settings.ChatWorkspaces.Add(emptyWorkspace);
Assert(ChatWorkspace.Reconcile(settings.ChatWorkspaces, Array.Empty<ChatSession>(), @"C:\default").Count == 0 && settings.ChatWorkspaces.Contains(emptyWorkspace), "Empty workspaces must survive without creating a session");
var root = Path.Combine(Path.GetTempPath(), "workspace-tests-" + Guid.NewGuid().ToString("N"));
try {
    var store = new ChatStore(root);
    store.Save(first, updateTimestamp: false);
    var restored = store.Load().Single();
    Assert(restored.WorkspaceId == first.WorkspaceId && restored.UpdatedAt.Year == 2020 && restored.Messages[0].ToJsonString() == first.Messages[0].ToJsonString(), "Session membership and transcript must survive reload");
    store.Delete(first);
    Assert(store.Load().Count == 0 && settings.ChatWorkspaces.Count == 3, "Deleting a last session must preserve its workspace");
}
finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
Console.WriteLine("PASS: legacy grouping, migration idempotence, shared directory changes, workspace expansion persistence, empty workspaces and session roundtrip");

var defaults = new List<ChatWorkspace>();
var defaultWorkspace = ChatWorkspace.EnsureDefault(defaults, @"C:\Users\test\OneDrive\Documents");
Assert(defaultWorkspace.Name == "デフォルト" && defaultWorkspace.Directory == @"C:\Users\test\OneDrive\Documents", "Default workspace must use the resolved Documents path and requested name");
Assert(ReferenceEquals(defaultWorkspace, ChatWorkspace.EnsureDefault(defaults, @"C:\Users\test\Documents")) && defaults.Count == 1, "Default workspace must not duplicate or overwrite existing configuration");
var preferences = new Settings { ConsoleExpanded = true, ConsoleHeight = 340, EditorWidthRatio = 0.62, ChatWorkspaces = defaults };
var savedPreferences = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(preferences))!;
Assert(savedPreferences.ConsoleExpanded == true && savedPreferences.ConsoleHeight == 340 && savedPreferences.EditorWidthRatio == 0.62, "Shared console visibility and pane boundaries must survive restart");
Assert(savedPreferences.ChatWorkspaces.Single().Name == "デフォルト" && savedPreferences.ChatWorkspaces.Single().IsDefault, "Default workspace label and identity must persist");
Assert(preferences.DefaultWorkspaceDirectory == Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Documents must be resolved by the environment, including redirection");
Console.WriteLine("PASS: redirected Documents default, default identity/name persistence and shared console/editor layout preferences");

var initialSettings = new Settings();
initialSettings.EnsureDefaultWorkspace();
Assert(initialSettings.ChatWorkspaces.Single().Name == "デフォルト", "Initial setup must create the Documents workspace");
initialSettings.ChatWorkspaces.Clear();
var afterDelete = JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(initialSettings))!;
afterDelete.EnsureDefaultWorkspace();
Assert(afterDelete.ChatWorkspaces.Count == 0 && afterDelete.DefaultWorkspaceInitialized, "Deleted default workspace must stay deleted after restart");
Console.WriteLine("PASS: deleted default workspace does not reappear on restart");
