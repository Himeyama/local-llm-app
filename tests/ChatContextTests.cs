using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalLlm.Gui.Services;

static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
static JsonObject Message(string role, string text) => new() { ["role"] = role, ["content"] = text };
static string Delta(JsonObject delta, string finish) => "data: " + new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["delta"] = delta, ["finish_reason"] = finish } } }.ToJsonString() + "\n\ndata: [DONE]\n\n";
static string Reply(string text, string finish = "stop") => Delta(new() { ["content"] = text }, finish);
static string Tool(string id, string arguments, string finish = "tool_calls") => Delta(new() { ["tool_calls"] = new JsonArray { new JsonObject { ["index"] = 0, ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = "browser", ["arguments"] = arguments } } } }, finish);
static ChatSession History(int count = 8) => new() { Messages = Enumerable.Range(0, count).Select(i => Message(i % 2 == 0 ? "user" : "assistant", "message-" + i)).ToList() };
var tools = new ChatTools("", false, false);

var readRoot = Path.Combine(Path.GetTempPath(), "chat-read-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(readRoot);
try {
    var reader = new ChatTools(readRoot, false, false);
    var fifty = string.Join("\r\n", Enumerable.Range(1, 50).Select(i => $"line {i}")) + "\r\n";
    var file = Path.Combine(readRoot, "lines.txt");
    await File.WriteAllTextAsync(file, fifty);
    Check(await reader.ExecuteAsync("read_file", new() { ["path"] = "lines.txt" }, default) == fifty, "Exactly 50 lines must not be truncated");
    await File.WriteAllTextAsync(file, fifty + "line 51");
    var limited = await reader.ExecuteAsync("read_file", new() { ["path"] = "lines.txt" }, default);
    Check(limited.StartsWith(fifty) && !limited.Contains("line 51") && limited.Contains("read_all"), "Default read must report omitted lines");
    Check(await reader.ExecuteAsync("read_file", new() { ["path"] = "lines.txt", ["read_all"] = true }, default) == fifty + "line 51", "Full read must preserve all content");
    var definition = reader.Definitions().Single(t => t?["function"]?["name"]?.GetValue<string>() == "read_file")!;
    Check(definition["function"]!["parameters"]!["properties"]!["read_all"]!["type"]!.GetValue<string>() == "boolean" && !definition["function"]!["parameters"]!["required"]!.AsArray().Any(x => x!.GetValue<string>() == "read_all"), "Full read option must be optional and advertised");
} finally { Directory.Delete(readRoot, true); }

using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(History(1), tools, new Quiet(), () => { }, default, "none");
    Check(handler.Requests[0]["reasoning_effort"]!.GetValue<string>() == "none" && !handler.Requests[0]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>(), "Off must disable thinking in the initial request");
    var system = handler.Requests[0]["messages"]![0]!["content"]!.GetValue<string>();
    Check(system.Contains("Python:") && system.Contains("Node.js:") && system.Contains("Windows PowerShell:") && system.Contains("execute_command is disabled"), "Local chat must receive runtime availability and command state");
}

// Explicit budgets work even when the model template ignores reasoning_effort.
foreach (var (effort, budget) in new[] { ("low", (int?)1536), ("medium", (int?)4096), ("xhigh", (int?)null), ("none", (int?)null) }) {
    using var handler = new ContextHandler(); using var http = new HttpClient(handler);
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("partial", "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(History(1), tools, new Quiet(), () => { }, default, effort);
    Check(handler.Requests[0]["reasoning_budget_tokens"]?.GetValue<int>() == budget, "Incorrect reasoning budget for " + effort);
    Check(handler.Requests[1]["reasoning_effort"]!.GetValue<string>() == "none" && handler.Requests[1]["reasoning_budget_tokens"] == null, "Continuation must disable reasoning without a budget");
}

// Partial replies are saved and merged, with continuation prompts sent only to the API.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(1);
    for (int i = 0; i < 5; i++) handler.Replies.Enqueue((HttpStatusCode.OK, Reply("part-" + i, "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    int saves = 0;
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => saves++, default);
    Check(saves == 6 && session.Messages.Count == 2 && session.Messages.Last()["content"]!.GetValue<string>() == "part-0part-1part-2part-3part-4done", "Long answer did not continue and merge");
    Check(handler.Requests[1]["messages"]!.AsArray().Last()!["role"]!.GetValue<string>() == "user", "Missing continuation instruction");
    Check(handler.Requests[1]["reasoning_effort"]!.GetValue<string>() == "none", "Recovery should avoid spending another budget on reasoning");
    Check(!handler.Requests[1]["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>(), "Recovery must disable thinking in the chat template");
}

// Reasoning-only exhaustion retries; a truncated tool batch must never execute.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    handler.Replies.Enqueue((HttpStatusCode.OK, Delta(new() { ["reasoning_content"] = "thinking" }, "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Tool("broken", "{\"action\":", "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Tool("complete", "{\"action\":\"read\"}")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    int executions = 0;
    var browser = new ChatTools("", true, false, browserTool: (a, ct) => { executions++; return Task.FromResult("page"); });
    var session = History(1);
    await new ChatClient(http).GenerateAsync(session, browser, new Quiet(), () => { }, default);
    Check(executions == 1 && session.Messages.All(m => m["tool_call_id"]?.GetValue<string>() != "broken"), "Truncated tool call was executed or saved");
    Check(handler.Requests[1]["max_tokens"]!.GetValue<int>() > handler.Requests[0]["max_tokens"]!.GetValue<int>(), "Recovery budget did not increase");
}

// Detect no progress, and preserve a partial answer when cancellation arrives.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    for (int i = 0; i < 3; i++) handler.Replies.Enqueue((HttpStatusCode.OK, Delta(new() { ["reasoning_content"] = "thinking" }, "length")));
    try { await new ChatClient(http).GenerateAsync(History(1), tools, new Quiet(), () => { }, default); throw new Exception("No-progress loop was not bounded"); }
    catch (IOException) { Check(handler.Requests.Count == 3, "Unexpected retry count"); }
}
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) using (var cancellation = new CancellationTokenSource()) {
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("saved partial", "length")));
    var session = History(1);
    try { await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => cancellation.Cancel(), cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { Check(session.Messages.Last()["content"]!.GetValue<string>() == "saved partial", "Partial answer lost on cancellation"); }
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply(" resumed")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(session.Messages.Count == 2 && session.Messages.Last()["content"]!.GetValue<string>() == "saved partial resumed" && session.Messages.Last()["incomplete"] == null, "Saved partial did not resume");
    Check(handler.Requests.Last()["messages"]!.AsArray().All(m => m?["incomplete"] == null), "Internal continuation metadata leaked into model request");
}
Console.WriteLine("PASS: long answer continuation, reasoning recovery, incomplete tool rejection, no-progress bound and cancellation");

// Known server usage triggers compaction; transcript and metadata survive a reload.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(); session.ContextTokens = 50000;
    var original = session.Messages.Select(m => m.ToJsonString()).ToArray();
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("compact summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("final answer")));
    var activity = new Activities();
    await new ChatClient(http).GenerateAsync(session, tools, activity, () => { }, default);
    Check(session.CompactedMessageCount == 4 && session.Summary == "compact summary", "Usage threshold did not compact");
    Check(original.SequenceEqual(session.Messages.Take(8).Select(m => m.ToJsonString())), "Automatic compaction deleted/changed the visible history");
    var next = handler.Requests[1]["messages"]!.ToJsonString();
    Check(next.Contains("compact summary") && !next.Contains("message-0") && next.Contains("message-4"), "Next request did not use summary plus recent messages");
    Check(handler.Requests[0]["tools"] == null && handler.Requests[0]["reasoning_effort"]!.GetValue<string>() == "none", "Summarizer used tools or lengthy reasoning");
    Check(activity.Values.Any(v => v.Activity.Contains("自動圧縮")), "Compression progress missing");
    var path = Path.Combine(Path.GetTempPath(), "chat-context-" + Guid.NewGuid().ToString("N"));
    try { var store = new ChatStore(path); store.Save(session); var loaded = store.Load().Single(); Check(loaded.CompactedMessageCount == 4 && loaded.Messages.Count == 9 && loaded.Summary == "compact summary", "Compaction state lost on reload"); }
    finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
}

// Fit old history into one summary request when the context has space.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(2); session.Messages[0]["content"] = new string('あ', 18000); session.ContextTokens = 50000;
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("one summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(handler.Requests.Count == 2 && session.CompactedMessageCount == 1 && session.Summary == "one summary", "History that fits required multiple summary requests");
}

// Oversized history is still summarized in bounded chunks.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(2); session.Messages[0]["content"] = new string('あ', 72000); session.ContextTokens = 50000;
    for (int i = 0; i < 3; i++) handler.Replies.Enqueue((HttpStatusCode.OK, Reply("summary-" + i)));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(handler.Requests.Count == 4 && session.CompactedMessageCount == 1 && session.Summary == "summary-2", "Oversized history not summarized in chunks");
    Check(handler.Requests.Take(3).All(r => r["messages"]!.AsArray().Sum(m => ChatCompaction.EstimateText(m!["content"]!.GetValue<string>())) < 50000), "Unbounded summarization input");
}

// Never start model context with an orphaned tool result.
var group = History(6);
group.Messages[1] = new JsonObject { ["role"] = "assistant", ["content"] = "", ["tool_calls"] = new JsonArray { new JsonObject { ["id"] = "pair" } } };
group.Messages[2] = new JsonObject { ["role"] = "tool", ["tool_call_id"] = "pair", ["content"] = "result" };
Check(ChatCompaction.Boundary(group) == 1, "Boundary split a tool-call/result pair");
var image = new ChatSession { Messages = new() { new JsonObject { ["role"] = "user", ["content"] = new JsonArray { new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = new string('A', 1000000) } } } } } };
Check(ChatCompaction.Estimate(image) < 10000, "Base64 counted as text tokens");

// A large tool result can force compaction during a running task, including
// the final batch if it alone exceeds the context budget.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(1);
    handler.Replies.Enqueue((HttpStatusCode.OK, Tool("large", "{\"action\":\"read\"}")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("user request summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("first tool summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("combined tool summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("task complete")));
    var browser = new ChatTools("", true, false, browserTool: (a, ct) => Task.FromResult(new string('x', 150000)));
    await new ChatClient(http).GenerateAsync(session, browser, new Quiet(), () => { }, default);
    Check(session.CompactedMessageCount == 3 && session.Messages.Count == 4, "Oversized tool batch was not summarized intact");
    Check(handler.Requests.Count == 5 && session.Summary == "combined tool summary", "Unexpected number of tool-result summary requests");
    Check(handler.Requests.Last()["messages"]!.AsArray().All(m => m?["role"]?.GetValue<string>() != "tool"), "Orphaned large tool result was resent");
    Check(session.Messages.Last()["content"]!.GetValue<string>() == "task complete", "Task did not continue after tool-result compaction");
}

// A server-side token count can exceed the estimate. Retry with smaller chunks.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(2); session.Messages[0]["content"] = new string('x', 2000); session.ContextTokens = 50000;
    handler.Replies.Enqueue((HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"exceed_context_size\"}}"));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("first half")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("combined summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(handler.Requests.Count == 4 && session.Summary == "combined summary", "Summary context overflow was not recovered by splitting");
}

// Failed or cancelled summarization cannot change the old summary/boundary/history.
foreach (bool cancel in new[] { false, true }) {
    using var handler = new ContextHandler(); using var http = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
    var session = History(); session.ContextTokens = 50000; session.Summary = "old summary";
    var original = session.Messages.Select(m => m.ToJsonString()).ToArray();
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply(cancel ? "new summary" : "")));
    if (cancel) handler.OnPost = () => cancellation.Cancel();
    try { await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, cancellation.Token); throw new Exception("Bad compaction accepted"); }
    catch (Exception e) when (e is IOException or OperationCanceledException) { }
    Check(session.Summary == "old summary" && session.CompactedMessageCount == 0 && original.SequenceEqual(session.Messages.Select(m => m.ToJsonString())), "Failed compression mutated source");
}

// A context-overflow response compacts once before retrying the real request.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    handler.Replies.Enqueue((HttpStatusCode.BadRequest, "{\"error\":{\"type\":\"exceed_context_size\"}}"));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("recovered summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("recovered answer")));
    var session = History();
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(session.CompactedMessageCount == 4 && session.Messages.Last()["content"]!.GetValue<string>() == "recovered answer", "Context overflow not recovered");
}
Console.WriteLine("PASS: usage threshold, bounded summaries, transcript persistence, tool boundaries, image estimate, failure atomicity and overflow recovery");

// Measured usage wins over a pessimistic estimate, avoiding premature compaction.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(2); session.Messages[0]["content"] = new string('あ', 36000); session.ContextTokens = 20000;
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(handler.Requests.Count == 1 && session.CompactedMessageCount == 0, "Estimate overrode measured usage");
}
Check(ChatCompaction.EstimateText(new string('あ', 1000)) == 1500, "Japanese estimate regressed to byte count");

// Discard truncated summaries, increase their budget, then split input if needed.
using (var handler = new ContextHandler()) using (var http = new HttpClient(handler)) {
    var session = History(2); session.Messages[0]["content"] = new string('あ', 2000); session.ContextTokens = 50000;
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("discard this", "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("discard again", "length")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("first half")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("combined summary")));
    handler.Replies.Enqueue((HttpStatusCode.OK, Reply("done")));
    await new ChatClient(http).GenerateAsync(session, tools, new Quiet(), () => { }, default);
    Check(session.Summary == "combined summary" && handler.Requests.Count == 5, "Summary splitting failed");
    Check(handler.Requests[1]["max_tokens"]!.GetValue<int>() > handler.Requests[0]["max_tokens"]!.GetValue<int>(), "Summary retry budget did not grow");
    Check(handler.Requests.Skip(1).All(r => !r["messages"]!.ToJsonString().Contains("discard this")), "Truncated summary reused");
}
Console.WriteLine("PASS: Japanese estimates, measured usage precedence, summary budget recovery and splitting");

sealed class Quiet : IProgress<ChatProgress> { public void Report(ChatProgress value) { } }
sealed class Activities : IProgress<ChatProgress> { public List<ChatProgress> Values { get; } = new(); public void Report(ChatProgress value) => Values.Add(value); }
sealed class ContextHandler : HttpMessageHandler
{
    public Queue<(HttpStatusCode Status, string Text)> Replies { get; } = new();
    public List<JsonObject> Requests { get; } = new();
    public Action? OnPost { get; set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"model_alias\":\"test-model\",\"default_generation_settings\":{\"n_ctx\":65536}}") };
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        OnPost?.Invoke();
        var reply = Replies.Dequeue();
        return new(reply.Status) { Content = new StringContent(reply.Text, Encoding.UTF8, "text/event-stream") };
    }
}
