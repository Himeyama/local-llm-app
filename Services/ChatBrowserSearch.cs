using System.Diagnostics;
using System.Text;
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
        var info = new ProcessStartInfo("node") {
            WorkingDirectory = folder, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.ArgumentList.Add(script);
        using var process = new Process { StartInfo = info };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("バックグラウンド検索を起動できません。Node.js 18 以降をインストールしてください。", ex); }
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
            var text = new StringBuilder("Source: " + data["source"]?.GetValue<string>() + "\n");
            foreach (var row in data["results"]!.AsArray())
                text.AppendLine(row!["title"]?.GetValue<string>()).AppendLine(row["url"]?.GetValue<string>()).AppendLine(row["snippet"]?.GetValue<string>()).AppendLine();
            return text.ToString();
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
}
