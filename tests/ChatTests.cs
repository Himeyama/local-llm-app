using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LocalLlm.Gui.Services;

static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
static async Task Reject(Func<Task> action, string message)
{
    try { await action(); } catch (Exception e) when (e is IOException or ArgumentException or InvalidDataException) { return; }
    throw new Exception(message);
}
static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();
static string Chunk(JsonObject delta, string? finish = null) => "data: " + new JsonObject { ["choices"] = new JsonArray { new JsonObject { ["delta"] = delta, ["finish_reason"] = finish } } }.ToJsonString() + "\n\n";
static string Call(int index, string? id, string? name, string fragment) => Chunk(new JsonObject { ["tool_calls"] = new JsonArray { new JsonObject { ["index"] = index, ["id"] = id, ["function"] = new JsonObject { ["name"] = name, ["arguments"] = fragment } } } });
static string Usage(int prompt, int completion) => "data: " + new JsonObject { ["choices"] = new JsonArray(), ["usage"] = new JsonObject { ["prompt_tokens"] = prompt, ["completion_tokens"] = completion, ["total_tokens"] = prompt + completion, ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = prompt / 2 } } }.ToJsonString() + "\n\n";

Assert(ChatContextUsage.Maximum(Args("{\"default_generation_settings\":{\"n_ctx\":32768},\"total_slots\":4}")) == 32768, "Maximum must use per-slot server context");
Assert(ChatContextUsage.Maximum(Args("{\"default_generation_settings\":{\"n_ctx\":0}}")) == null, "Invalid maximum context accepted");
Assert(ChatContextUsage.Maximum(Args("{}")) == null, "Unavailable maximum must remain unknown");
Assert(ChatContextUsage.Total(Args("{\"prompt_tokens\":90,\"completion_tokens\":10}")) == 100, "Split token usage not summed");
Assert(ChatContextUsage.Total(Args("{\"total_tokens\":-1}")) == null, "Negative token usage accepted");
Assert(ChatContextUsage.Total(Args("{\"prompt_tokens\":2147483647,\"completion_tokens\":1}")) == null, "Overflow token usage accepted");
Assert(new[] { "none", "low", "medium", "xhigh" }.Select(ChatContextUsage.ReasoningLabel).SequenceEqual(new[] { "オフ", "低", "中", "最高" }), "Reasoning labels incorrect");
Assert(ChatCommands.Parse(" /clear ") == "clear" && ChatCommands.Parse("/compress") == "compress" && ChatCommands.Parse("/compact") == "compress", "Chat command aliases incorrect");
Assert(ChatCommands.Parse("/clear something") == null && ChatCommands.Parse("通常の会話") == null, "Ordinary messages interpreted as commands");

var root = Path.Combine(Path.GetTempPath(), "LocalLlmChat-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var tools = new ChatTools(root, false, true, Path.Combine(root, "chat-backups"));
    await tools.ExecuteAsync("write_file", Args("{\"path\":\"notes/a.txt\",\"content\":\"こんにちは\\nworld\"}"), default);
    Assert(await tools.ExecuteAsync("read_file", Args("{\"path\":\"notes/a.txt\"}"), default) == "こんにちは\nworld", "UTF-8 create/read failed");
    Assert((await tools.ExecuteAsync("search_files", Args("{\"query\":\"world\"}"), default)).Contains("a.txt:2"), "Content search lost line number");
    var edit = await tools.ExecuteAsync("edit_file", Args("{\"path\":\"notes/a.txt\",\"old_text\":\"world\",\"new_text\":\"世界\"}"), default);
    Assert(File.ReadAllText(Path.Combine(root, "notes/a.txt")) == "こんにちは\n世界", "Edit failed");
    var backup = edit.Split('\n')[1]["元ファイル: ".Length..];
    Assert(File.ReadAllText(backup) == "こんにちは\nworld", "Backup failed");
    File.Delete(backup);
    await Reject(() => tools.ExecuteAsync("read_file", Args("{\"path\":\"../escape.txt\"}"), default), "Traversal accepted");
    await Reject(() => tools.ExecuteAsync("write_file", Args("{\"path\":\".git/config\",\"content\":\"bad\"}"), default), "Protected directory accepted");
    await Reject(() => tools.ExecuteAsync("edit_file", Args("{\"path\":\"notes/a.txt\",\"old_text\":\"missing\",\"new_text\":\"x\"}"), default), "Missing edit match accepted");
    File.WriteAllText(Path.Combine(root, "repeat.txt"), "same same");
    await Reject(() => tools.ExecuteAsync("edit_file", Args("{\"path\":\"repeat.txt\",\"old_text\":\"same\",\"new_text\":\"x\"}"), default), "Ambiguous edit accepted");
    File.WriteAllBytes(Path.Combine(root, "binary.dat"), new byte[] { 0, 1, 2 });
    await Reject(() => tools.ExecuteAsync("write_file", Args("{\"path\":\"binary.dat\",\"content\":\"bad\"}"), default), "Binary overwrite accepted");
    var readonlyTools = new ChatTools(root, false, false);
    await Reject(() => readonlyTools.ExecuteAsync("write_file", Args("{\"path\":\"blocked.txt\",\"content\":\"bad\"}"), default), "Writes disabled but executed");
    await Reject(() => readonlyTools.ExecuteAsync("web_read", Args("{\"url\":\"https://example.com\"}"), default), "Web disabled but executed");
    var webTools = new ChatTools(root, true, false);
    await Reject(() => webTools.ExecuteAsync("web_search", Args("{\"query\":\"\"}"), default), "Empty browser search accepted");
    await Reject(() => webTools.ExecuteAsync("web_search", new JsonObject { ["query"] = new string('x', 2001) }, default), "Oversized browser search accepted");
    using (var searchCancellation = new CancellationTokenSource())
    {
        searchCancellation.Cancel();
        try { await webTools.ExecuteAsync("web_search", Args("{\"query\":\"test\"}"), searchCancellation.Token); throw new Exception("Browser search cancellation ignored"); }
        catch (OperationCanceledException) { }
    }
    await Reject(() => webTools.ExecuteAsync("web_read", Args("{\"url\":\"http://127.0.0.1:9931/props\"}"), default), "Loopback web access accepted");
    await Reject(() => webTools.ExecuteAsync("web_read", Args("{\"url\":\"file:///C:/Windows/win.ini\"}"), default), "File URL accepted");
    var junction = Path.Combine(root, "junction");
    using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe") { Arguments = $"/c mklink /J \"{junction}\" \"{Path.Combine(root, "notes")}\"", CreateNoWindow = true, RedirectStandardOutput = true })) { process!.WaitForExit(); Assert(process.ExitCode == 0, "Test junction creation failed"); }
    await Reject(() => tools.ExecuteAsync("read_file", Args("{\"path\":\"junction/a.txt\"}"), default), "Junction traversal accepted");
    Directory.Delete(junction);
    Console.WriteLine("PASS: UTF-8 file create/read/search/edit, backups, traversal/junction protection, ambiguous edit, binary and disabled tool rejection, public URL checks");

    var store = new ChatStore(Path.Combine(root, "history"));
    var session = new ChatSession { Workspace = root, Title = "画像チャット", Messages = new() { Args("{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"画像を確認\"},{\"type\":\"image_url\",\"image_url\":{\"url\":\"data:image/png;base64,AAAA\"}}]}" ) } };
    store.Save(session); File.WriteAllText(Path.Combine(root, "history/broken.json"), "{broken");
    var reloaded = store.Load().Single();
    Assert(reloaded.Id == session.Id && reloaded.Messages[0].ToJsonString() == session.Messages[0].ToJsonString(), "History/image roundtrip failed");
    Console.WriteLine("PASS: session persistence, image content roundtrip, corrupted history isolation");
    var disposableSession = new ChatSession { Title = "削除テスト" }; store.Save(disposableSession);
    store.Delete(disposableSession);
    Assert(store.Load().Count == 1 && store.Load().Single().Id == session.Id, "Delete must preserve other sessions");
    Assert(!File.Exists(Path.Combine(root, "history", disposableSession.Id + ".json")), "Deleted history still exists");
    await Reject(() => { store.Delete(new ChatSession { Id = "../escape" }); return Task.CompletedTask; }, "Unsafe deletion ID accepted");
    Console.WriteLine("PASS: session deletion persists and preserves other histories, invalid ID rejected");

    var handler = new MockHandler();
    handler.Responses.Enqueue(Call(0, "call_1", "read_file", "{\"path\":") + Call(0, null, null, "\"notes/a.txt\"}") + Chunk(new(), "tool_calls") + Usage(100, 20) + "data: [DONE]\n\n");
    handler.Responses.Enqueue(Chunk(new() { ["content"] = "日本語" }) + Chunk(new() { ["content"] = "の回答" }, "stop") + Usage(250, 30) + "data: [DONE]\n\n");
    using var client = new HttpClient(handler);
    int saves = 0;
    await new ChatClient(client).GenerateAsync(session, readonlyTools, new SilentProgress(), () => { saves++; store.Save(session); }, default, "low");
    Assert(saves == 2 && session.Messages.Count == 4, "Tool cycle persistence failed");
    Assert(session.Messages[2]["role"]!.GetValue<string>() == "tool" && session.Messages[2]["content"]!.GetValue<string>().Contains("世界"), "Tool result missing");
    Assert(session.Messages[3]["content"]!.GetValue<string>() == "日本語の回答", "Stream assembly failed");
    Assert(handler.Requests.All(r => r["model"]!.GetValue<string>() == "server-model"), "Model ID must come from props");
    Assert(handler.Requests.All(r => r["reasoning_effort"]!.GetValue<string>() == "low"), "Reasoning level must reach every model/tool turn");
    Assert(handler.Requests.All(r => r["stream_options"]!["include_usage"]!.GetValue<bool>()), "Streaming usage must be requested for every round");
    Assert(session.ContextTokens == 280 && store.Load().Single().ContextTokens == 280, "Latest usage must include cached tokens, replace previous tool-round usage, and persist");
    Console.WriteLine("PASS: context limit, usage-only SSE chunks, latest-round token totals, persistence and reasoning labels");
    Assert(handler.Requests[1]["messages"]!.AsArray().Any(m => m?["tool_call_id"]?.GetValue<string>() == "call_1"), "Tool result not sent to model");
    Assert(handler.Requests[0]["messages"]![1]!["content"]![1]!["image_url"]!["url"]!.GetValue<string>().StartsWith("data:image/png"), "Image not sent to model");
    Console.WriteLine("PASS: props model discovery, image payload, fragmented SSE tool calls, execution and next-turn tool results, final streamed reply");

    var interrupted = new ChatSession { Workspace = root, Messages = new() { Args("{\"role\":\"user\",\"content\":\"test\"}") } };
    await Reject(() => new ChatClient(client).GenerateAsync(interrupted, readonlyTools, new SilentProgress(), () => { }, default, "invalid"), "Invalid reasoning level accepted");
    handler.Responses.Enqueue(Chunk(new() { ["content"] = "partial" }));
    await Reject(() => new ChatClient(client).GenerateAsync(interrupted, readonlyTools, new SilentProgress(), () => { }, default), "Truncated stream accepted");
    Assert(interrupted.Messages.Count == 1, "Interrupted assistant should not enter history");
    handler.Responses.Enqueue(Chunk(new() { ["content"] = "too long" }, "length") + "data: [DONE]\n\n");
    await Reject(() => new ChatClient(client).GenerateAsync(interrupted, readonlyTools, new SilentProgress(), () => { }, default), "Length-limited response accepted");
    handler.Responses.Enqueue(Call(0, "failed", "read_file", "{\"path\":\"../no\"}") + Chunk(new(), "tool_calls") + "data: [DONE]\n\n");
    handler.Responses.Enqueue(Chunk(new() { ["content"] = "失敗しました" }, "stop") + "data: [DONE]\n\n");
    await new ChatClient(client).GenerateAsync(interrupted, readonlyTools, new SilentProgress(), () => { }, default);
    Assert(interrupted.ContextTokens == null, "Missing usage must remain unknown");
    Assert(interrupted.Messages[2]["content"]!.GetValue<string>().Contains("ツール失敗"), "Tool failure not returned to model");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { await new ChatClient(client).GenerateAsync(interrupted, readonlyTools, new SilentProgress(), () => { }, cancelled.Token); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { }
    Console.WriteLine("PASS: disconnection, generation limit, tool errors, cancellation");

    var compressionHandler = new MockHandler();
    using var compressionHttp = new HttpClient(compressionHandler);
    var compressionClient = new ChatClient(compressionHttp);
    var before = new ChatSession { Workspace = root, Title = "継続中", Summary = "以前の要約", ContextTokens = 900,
        Messages = new() { Args("{\"role\":\"user\",\"content\":\"設定ファイルを更新。次はテスト。\"}"), Args("{\"role\":\"assistant\",\"content\":\"設定更新は完了しました。\"}") } };
    var originalMessages = string.Join("\n", before.Messages.Select(m => m.ToJsonString()));
    compressionHandler.Responses.Enqueue(Chunk(new() { ["content"] = "設定更新済み。次はテスト。" }, "stop") + Usage(900, 30) + "data: [DONE]\n\n");
    var compressed = await ChatCommands.CompressAsync(before, compressionClient, new SilentProgress(), default, "low");
    Assert(compressed.Id == before.Id && compressed.Title == before.Title && compressed.Workspace == root && compressed.Messages.Count == 0 && compressed.Summary == "設定更新済み。次はテスト。" && compressed.ContextTokens == null, "Compressed session metadata or summary incorrect");
    Assert(string.Join("\n", before.Messages.Select(m => m.ToJsonString())) == originalMessages && before.Summary == "以前の要約" && before.ContextTokens == 900, "Compression changed source before success");
    Assert(compressionHandler.Requests[0]["tools"] == null && compressionHandler.Requests[0]["tool_choice"] == null, "Compression must not enable tools");
    Assert(compressionHandler.Requests[0]["messages"]!.AsArray().Any(m => m?["content"]?.ToString().Contains("以前の要約") == true), "Prior summary lost during recompression");
    store.Save(compressed);
    Assert(store.Load().Single(s => s.Id == compressed.Id).Summary == compressed.Summary, "Summary persistence failed");
    compressionHandler.Responses.Enqueue(Chunk(new() { ["content"] = "続きを実行します。" }, "stop") + "data: [DONE]\n\n");
    compressed.Messages.Add(Args("{\"role\":\"user\",\"content\":\"続けて\"}"));
    await compressionClient.GenerateAsync(compressed, readonlyTools, new SilentProgress(), () => { }, default);
    Assert(compressionHandler.Requests[1]["messages"]!.AsArray().Any(m => m?["role"]?.GetValue<string>() == "user" && m["content"]?.ToString().Contains("設定更新済み") == true), "Summary not included in next request");
    compressionHandler.Responses.Enqueue(Chunk(new() { ["content"] = "" }, "stop") + "data: [DONE]\n\n");
    await Reject(() => ChatCommands.CompressAsync(before, compressionClient, new SilentProgress(), default, "low"), "Empty summary accepted");
    compressionHandler.Responses.Enqueue(Chunk(new() { ["content"] = "途中の要約" }));
    await Reject(() => ChatCommands.CompressAsync(before, compressionClient, new SilentProgress(), default, "low"), "Interrupted summary accepted");
    compressionHandler.Responses.Enqueue(Call(0, "unwanted", "read_file", "{\"path\":\"notes/a.txt\"}") + Chunk(new(), "tool_calls") + "data: [DONE]\n\n");
    await Reject(() => ChatCommands.CompressAsync(before, compressionClient, new SilentProgress(), default, "low"), "Tool call accepted during compression");
    try { await ChatCommands.CompressAsync(before, compressionClient, new SilentProgress(), cancelled.Token, "low"); throw new Exception("Compression cancellation ignored"); } catch (OperationCanceledException) { }
    Assert(string.Join("\n", before.Messages.Select(m => m.ToJsonString())) == originalMessages && before.Summary == "以前の要約", "Failed compression changed original conversation");
    var cleared = ChatCommands.Clear(before);
    Assert(cleared.Id == before.Id && cleared.Workspace == root && cleared.Title == "新しいチャット" && cleared.Summary == "" && cleared.Messages.Count == 0 && cleared.ContextTokens == 0, "Clear did not reset conversation while preserving identity/workspace");
    Console.WriteLine("PASS: clear, compress/compact aliases, tool-free summarization, saved summary continuation, failure/cancellation preserves source");
    if (args.Contains("--web"))
    {
        var page = await webTools.ExecuteAsync("web_read", Args("{\"url\":\"https://example.com\"}"), default);
        Assert(page.Contains("Example Domain") && page.Contains("Source:"), "Public web read failed");
        Console.WriteLine("PASS: real public HTTPS page retrieval and text extraction");
        try
        {
            var results = await webTools.ExecuteAsync("web_search", Args("{\"query\":\"llama.cpp github\"}"), default);
            Assert(results.Contains("https://"), "Search source URLs missing");
            Console.WriteLine("PASS: real web search and source URLs");
        }
        catch (IOException e) { Console.WriteLine("Web search provider unavailable: " + e.Message); }
        using var activeSearchCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopWatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await webTools.ExecuteAsync("web_search", Args("{\"query\":\"llama.cpp github\"}"), activeSearchCancellation.Token);
            throw new Exception("Active browser search cancellation ignored");
        }
        catch (OperationCanceledException) { }
        Assert(stopWatch.Elapsed < TimeSpan.FromSeconds(10), "Browser search did not stop promptly");
        Console.WriteLine("PASS: active browser search cancellation and process cleanup");
    }
}
finally { Directory.Delete(root, true); }

sealed class SilentProgress : IProgress<ChatProgress> { public void Report(ChatProgress value) { } }
sealed class MockHandler : HttpMessageHandler
{
    public Queue<string> Responses { get; } = new();
    public List<JsonObject> Requests { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = new StringContent("{\"model_alias\":\"server-model\"}", Encoding.UTF8, "application/json") };
        Requests.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject());
        return new(HttpStatusCode.OK) { Content = new StringContent(Responses.Dequeue(), Encoding.UTF8, "text/event-stream") };
    }
}
