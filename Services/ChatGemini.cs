using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

// The configured URL is the complete proxy endpoint, not a Google SDK base URL.
public sealed class GeminiChatClient(HttpClient client, string url, string authMethod = "none", string apiKey = "")
{
    public async Task GenerateAsync(ChatSession session, ChatTools tools, IProgress<ChatProgress> progress, Action save, CancellationToken ct, string reasoningEffort = "xhigh")
    {
        string thinkingLevel = reasoningEffort switch { "none" => "minimal", "low" => "low", "medium" => "medium", "xhigh" => "high", _ => throw new ArgumentException("未対応の推論レベルです。") };
        JsonArray contents = new();
        if (session.Summary.Length > 0) contents.Add(Content("user", new JsonObject { ["text"] = "会話の要約: " + session.Summary }));
        foreach (JsonObject message in session.Messages.Skip(ChatCompaction.Start(session)))
        {
            string role = message["role"]?.GetValue<string>() ?? "";
            if (role == "tool") {
                contents.Add(Content("user", new JsonObject { ["text"] = "以前のツール結果: " + message["content"]?.GetValue<string>() }));
                continue;
            }
            JsonArray parts = new();
            if (message["content"] is JsonValue text && text.TryGetValue<string>(out string? value) && !string.IsNullOrEmpty(value)) parts.Add(new JsonObject { ["text"] = value });
            if (message["content"] is JsonArray attachments) foreach (JsonNode? attachment in attachments) {
                if (attachment?["type"]?.GetValue<string>() == "text") parts.Add(new JsonObject { ["text"] = attachment["text"]?.GetValue<string>() });
                else if (attachment?["image_url"]?["url"]?.GetValue<string>() is string data && data.StartsWith("data:", StringComparison.Ordinal)) {
                    int comma = data.IndexOf(','); int semicolon = data.IndexOf(';');
                    if (comma > 0 && semicolon > 5 && semicolon < comma) parts.Add(new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = data[5..semicolon], ["data"] = data[(comma + 1)..] } });
                }
            }
            if (message["tool_calls"] is JsonArray calls) parts.Add(new JsonObject { ["text"] = "以前のツール呼び出し: " + calls.ToJsonString() });
            if (parts.Count > 0) contents.Add(new JsonObject { ["role"] = role == "assistant" ? "model" : "user", ["parts"] = parts });
        }
        JsonArray declarations = new();
        foreach (JsonNode? item in tools.Definitions()) {
            JsonObject function = (JsonObject)item!["function"]!.DeepClone();
            function["parameters"]?.AsObject().Remove("additionalProperties");
            declarations.Add(function);
        }
        for (int turn = 0; turn < 32; turn++)
        {
            JsonObject request = new() { ["contents"] = contents.DeepClone(), ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray { new JsonObject { ["text"] = "Reply in the user's language. Use tools when needed; never pretend to access files. Workspace: " + session.Workspace } } }, ["tools"] = new JsonArray { new JsonObject { ["functionDeclarations"] = declarations.DeepClone() } } };
            request["generationConfig"] = new JsonObject { ["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = thinkingLevel, ["includeThoughts"] = true } };
            using HttpRequestMessage httpRequest = new(HttpMethod.Post, url) { Content = JsonContent.Create(request) };
            switch (authMethod)
            {
                case "none": break;
                case "bearer": httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey); break;
                case "api-key": httpRequest.Headers.Add("api-key", apiKey); break;
                case "x-api-key": httpRequest.Headers.Add("x-api-key", apiKey); break;
                default: throw new ArgumentException("未対応の認証方式です。");
            }
            httpRequest.Headers.Accept.ParseAdd("text/event-stream");
            using HttpResponseMessage response = await client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge) throw new IOException("Gemini への送信内容が大きすぎます。添付画像を小さくするか、新しいチャットで再送信してください。");
            if (!response.IsSuccessStatusCode) throw new IOException($"Gemini: {(int)response.StatusCode}\n" + await response.Content.ReadAsStringAsync(ct));
            using StreamReader reader = new(await response.Content.ReadAsStreamAsync(ct));
            StringBuilder data = new(), answer = new(), reasoning = new(); JsonArray modelParts = new(); string? finish = null; bool received = false;
            async Task ProcessEventAsync() {
                if (data.Length == 0) return;
                string json = data.ToString(); data.Clear(); if (json == "[DONE]") return;
                JsonNode? chunk = JsonNode.Parse(json); received = true;
                if (chunk?["error"] is JsonNode error) throw new IOException(error.ToJsonString());
                if (chunk?["usageMetadata"]?["totalTokenCount"]?.GetValue<int>() is int tokens) session.ContextTokens = tokens;
                JsonNode? candidate = chunk?["candidates"]?.AsArray().FirstOrDefault();
                finish = candidate?["finishReason"]?.GetValue<string>() ?? finish;
                if (candidate?["content"]?["parts"] is JsonArray parts) foreach (JsonNode? part in parts) {
                    if (part?["text"]?.GetValue<string>() is string fragment) { if (part["thought"]?.GetValue<bool>() == true) reasoning.Append(fragment); else answer.Append(fragment); }
                    if (part != null) modelParts.Add(part.DeepClone());
                }
                progress.Report(new(answer.ToString(), reasoning.Length > 0 && answer.Length == 0 ? "考えています…" : "回答を生成しています…", session.ContextTokens, reasoning.ToString()));
                await Task.CompletedTask;
            }
            while (await reader.ReadLineAsync(ct) is string line) {
                if (line.Length == 0) { await ProcessEventAsync(); continue; }
                if (line.StartsWith("data:", StringComparison.Ordinal)) { if (data.Length > 0) data.Append('\n'); data.Append(line[5..].TrimStart(' ')); }
            }
            await ProcessEventAsync();
            if (!received || finish == null) throw new IOException("Gemini の応答が完了前に切断されました。再送信してください。");
            if (finish != "STOP" && finish != "MAX_TOKENS") throw new IOException("Gemini の生成が終了しました: " + finish);
            if (finish == "MAX_TOKENS") throw new IOException("Gemini の生成が上限に達しました。入力を短くして再送信してください。");
            JsonArray calls = new();
            foreach (JsonNode? part in modelParts) if (part?["functionCall"] is JsonObject call) calls.Add(call.DeepClone());
            if (calls.Count == 0) {
                var assistant = new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString() };
                if (reasoning.Length > 0) assistant["reasoning_content"] = reasoning.ToString();
                session.Messages.Add(assistant); save(); progress.Report(new("", "完了")); return;
            }
            contents.Add(new JsonObject { ["role"] = "model", ["parts"] = modelParts });
            JsonArray results = new(); JsonArray storedCalls = new();
            foreach (JsonNode? call in calls) {
                string name = call!["name"]?.GetValue<string>() ?? ""; JsonObject arguments = call["args"] as JsonObject ?? new(); string id = Guid.NewGuid().ToString("N");
                progress.Report(new(answer.ToString(), "実行中: " + name, session.ContextTokens, reasoning.ToString()));
                string result; try { result = await tools.ExecuteAsync(name, arguments, ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; } catch (Exception ex) { result = "ツール失敗: " + ex.Message; }
                storedCalls.Add(new JsonObject { ["id"] = id, ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = arguments.ToJsonString() } });
                results.Add(new JsonObject { ["functionResponse"] = new JsonObject { ["name"] = name, ["response"] = new JsonObject { ["result"] = result } } });
                session.Messages.Add(new JsonObject { ["role"] = "tool", ["name"] = name, ["tool_call_id"] = id, ["content"] = result });
            }
            var toolAssistant = new JsonObject { ["role"] = "assistant", ["content"] = answer.ToString(), ["tool_calls"] = storedCalls };
            if (reasoning.Length > 0) toolAssistant["reasoning_content"] = reasoning.ToString();
            session.Messages.Insert(session.Messages.Count - calls.Count, toolAssistant);
            save(); contents.Add(new JsonObject { ["role"] = "user", ["parts"] = results });
        }
        throw new IOException("Gemini のツール呼び出しが上限に達しました。会話は保持しています。");
    }
    static JsonObject Content(string role, JsonObject part) => new() { ["role"] = role, ["parts"] = new JsonArray { part } };
}
