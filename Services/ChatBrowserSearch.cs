using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public static class ChatBrowserSearch
{
    public static async Task<string> SearchAsync(string query, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || query.Length > 2000) throw new ArgumentException("検索語を 1～2,000 文字で指定してください。");
        var folder = Path.Combine(Settings.ResolveRoot(AppContext.BaseDirectory), "BrowserSearch");
        var script = Path.Combine(folder, "search.mjs");
        if (!File.Exists(script) || !Directory.Exists(Path.Combine(folder, "node_modules", "@playwright", "mcp")))
            throw new IOException("Playwright MCP が未セットアップです。BrowserSearch フォルダーで npm ci を実行してください。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var info = new ProcessStartInfo(Settings.NodeExecutable) {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add(script);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("バックグラウンド検索を起動できません。Node.js 20 以降をインストールしてください。", ex); }
        try
        {
            // Read both pipes immediately so diagnostic output cannot deadlock.
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(new JsonObject { ["query"] = query }.ToJsonString().AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result = await output; var detail = await error;
            if (process.ExitCode != 0) throw new IOException("Playwright MCP 検索に失敗しました: " + detail[..Math.Min(detail.Length, 2000)]);
            var data = JsonNode.Parse(result) ?? throw new IOException("検索結果が空です。");
            return FormatResults(data);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Playwright MCP 検索がタイムアウトしました。"); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public static string FormatResults(JsonNode data)
    {
        var text = new StringBuilder();
        text.AppendLine("検索語: " + data["query"]?.GetValue<string>());
        text.AppendLine("検索ページ: " + data["source"]?.GetValue<string>());
        text.AppendLine("取得日時: " + data["retrievedAt"]?.GetValue<string>());
        text.AppendLine("以下は検索ページ上の見出しと抜粋です。リンク先の本文は未確認です。");
        var rows = data["results"] as JsonArray ?? throw new IOException("検索結果の形式が不正です。");
        for (int index = 0; index < rows.Count; index++)
            AppendResult(text, rows[index], index + 1);
        return text.ToString();
    }

    // Older chats store one escaped JSON object per result. Decode only those
    // objects for display; do not rewrite persisted history or literal escapes.
    public static string FormatForDisplay(string result)
    {
        var text = new StringBuilder();
        int count = 0;
        foreach (var line in result.Replace("\r\n", "\n").Split('\n'))
        {
            JsonNode? row = null;
            if (line.TrimStart().StartsWith('{'))
            {
                try { row = JsonNode.Parse(line); }
                catch (JsonException) { }
            }
            if (row is JsonObject && row["title"] is JsonValue title && title.TryGetValue<string>(out _)
                && row["url"] is JsonValue url && url.TryGetValue<string>(out _)
                && (row["snippet"] is null || row["snippet"] is JsonValue snippet && snippet.TryGetValue<string>(out _)))
                AppendResult(text, row, ++count);
            else
                text.AppendLine(line.StartsWith("Source: ", StringComparison.Ordinal) ? "検索ページ: " + line[8..] : line);
        }
        return count > 0 ? text.ToString().TrimEnd('\r', '\n') : result;
    }

    private static void AppendResult(StringBuilder text, JsonNode? row, int number)
    {
        text.AppendLine();
        text.AppendLine($"{number}. {row?["title"]?.GetValue<string>()}");
        text.AppendLine("リンク: " + row?["url"]?.GetValue<string>());
        var snippet = row?["snippet"]?.GetValue<string>();
        text.AppendLine("抜粋: " + (string.IsNullOrWhiteSpace(snippet) ? "（抜粋なし）" : snippet));
    }
}
