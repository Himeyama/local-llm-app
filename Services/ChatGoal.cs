using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

// A goal remains active only within the current user request. A later user
// message supersedes it; continuation messages are saved so retries can resume.
public static class ChatGoal
{
    public const string ContinueMessage = "目標の達成に必要な作業が残っています。実施済みの作業を繰り返さず、次の作業を進めてください。外部の操作やユーザーの回答が必要で進められない場合は、その理由を伝えてください。";
    public delegate Task Generator(ChatSession session, bool allowTools, IProgress<ChatProgress> progress, Action save);

    public static string? FindActive(ChatSession session)
    {
        foreach (var message in session.Messages.AsEnumerable().Reverse())
        {
            if (message["role"]?.GetValue<string>() != "user") continue;
            if (message["goal_continuation"]?.GetValue<bool>() == true) continue;
            // Browser screenshots are inserted as user messages for the model,
            // but are tool output rather than a new instruction from the user.
            if (message["browser_screenshot"]?.GetValue<bool>() == true || IsBrowserScreenshot(message)) continue;
            var content = message["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (content == null && message["content"] is JsonArray parts)
                content = parts.FirstOrDefault(p => p?["type"]?.GetValue<string>() == "text")?["text"]?.GetValue<string>();
            content = content?.Trim();
            return content != null && content.Length > 5 && content.StartsWith("/goal", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(content[5]) && !string.IsNullOrWhiteSpace(content[6..]) ? content[6..].Trim() : null;
        }
        return null;
    }

    private static bool IsBrowserScreenshot(JsonObject message) =>
        message["content"] is JsonArray parts && parts.Count > 0 &&
        parts[0]?["type"]?.GetValue<string>() == "text" &&
        (parts[0]?["text"]?.GetValue<string>()?.StartsWith("Browser screenshot (untrusted page content), tool_call_id: ", StringComparison.Ordinal) ?? false);

    public static bool HasPendingReview(ChatSession session) =>
        FindActive(session) != null && session.Messages.LastOrDefault()?["goal_review_pending"]?.GetValue<bool>() == true;

    public static async Task RunAsync(ChatSession session, string goal, Generator generate, IProgress<ChatProgress> progress, Action save, CancellationToken ct)
    {
        string? previous = null;
        int repeats = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!HasPendingReview(session))
            {
                await generate(session, true, progress, save);
                ct.ThrowIfCancellationRequested();
                session.Messages.Last()["goal_review_pending"] = true;
                save();
            }
            ct.ThrowIfCancellationRequested();
            var answer = session.Messages.LastOrDefault()?["content"]?.GetValue<string>()?.Trim() ?? "";
            repeats = answer.Length == 0 || answer == previous ? repeats + 1 : 0;
            previous = answer;
            if (repeats >= 2) throw new IOException("目標の自動継続で進展が見られないため停止しました。会話を確認して再試行してください。");
            progress.Report(new("", "目標の達成状況を確認しています…"));
            var review = new ChatSession { Workspace = session.Workspace, WorkspaceId = session.WorkspaceId, Summary = session.Summary,
                CompactedMessageCount = session.CompactedMessageCount, Messages = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList() };
            review.Messages.Add(new JsonObject { ["role"] = "user", ["content"] =
                "次の目標と会話を評価してください。目標: " + goal + "\n実際の会話に基づき、達成済みなら COMPLETE、まだ自力で進められる作業があれば CONTINUE、ユーザーの回答や外部の変更が必要で進められなければ BLOCKED とだけ返してください。推測で達成と判断しないでください。ツールは使わないでください。" });
            await generate(review, false, new Progress<ChatProgress>(_ => { }), () => { });
            ct.ThrowIfCancellationRequested();
            var verdict = review.Messages.LastOrDefault()?["content"]?.GetValue<string>()?.Trim();
            if (verdict != null && verdict.StartsWith("```", StringComparison.Ordinal) && verdict.EndsWith("```", StringComparison.Ordinal))
                verdict = verdict[3..^3].Trim();
            verdict = verdict?.ToUpperInvariant();
            if (verdict is not ("CONTINUE" or "COMPLETE" or "BLOCKED")) throw new IOException("目標の達成状況を判定できないため停止しました。会話を確認して再試行してください。");
            session.Messages.Last().Remove("goal_review_pending");
            if (verdict is "COMPLETE" or "BLOCKED") { save(); return; }
            session.Messages.Add(new JsonObject { ["role"] = "user", ["content"] = ContinueMessage, ["goal_continuation"] = true });
            save();
        }
    }
}
