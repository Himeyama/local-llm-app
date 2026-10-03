using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public static class ChatCommands
{
    public static string? Parse(string text) => text.Trim().ToLowerInvariant() switch {
        "/clear" => "clear", "/compress" or "/compact" => "compress", _ => null
    };

    public static ChatSession Clear(ChatSession source) => new() {
        Id = source.Id, Title = "新しいチャット", Workspace = source.Workspace, ContextTokens = 0
    };

    public static async Task<ChatSession> CompressAsync(ChatSession source, ChatClient client, IProgress<ChatProgress> progress, CancellationToken ct, string reasoningEffort)
    {
        if (source.Messages.Count == 0) throw new ArgumentException("要約する新しい会話がありません。");
        // Work on a clone so cancellation, empty output and server errors never
        // replace the user's conversation with an incomplete summary.
        var temporary = new ChatSession { Workspace = source.Workspace, Summary = source.Summary,
            Messages = source.Messages.Select(m => (JsonObject)m.DeepClone()).ToList() };
        temporary.Messages.Add(new JsonObject { ["role"] = "user", ["content"] =
            "これまでの会話を、次の会話へ引き継ぐための短い要約に圧縮してください。要約だけを返してください。" +
            "ユーザーの目的・指示・制約、確定した事実と判断、実施済みの作業と結果、未完了の作業、重要なファイルパス・URL・数値を保持してください。" +
            "画像に関する重要な観察も文章で保持してください。ツールや検索を実行せず、元の会話より十分短く、できれば日本語800文字以内にまとめてください。" });
        await client.GenerateAsync(temporary, new ChatTools(source.Workspace, false, false), progress, () => { }, ct, reasoningEffort, allowTools: false);
        ct.ThrowIfCancellationRequested();
        var summary = temporary.Messages.Last()["content"]?.GetValue<string>()?.Trim();
        if (string.IsNullOrEmpty(summary)) throw new IOException("要約が空のため、元の会話を保持しました。");
        return new ChatSession { Id = source.Id, Title = source.Title, Workspace = source.Workspace, Summary = summary };
    }
}
