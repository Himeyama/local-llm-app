using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public static class ChatInterruption
{
    public static void CompleteTranscript(ChatSession session)
    {
        var last = session.Messages.LastOrDefault();
        if (last?["role"]?.GetValue<string>() == "assistant")
        {
            if (last["incomplete"]?.GetValue<bool>() == true)
            {
                last.Remove("incomplete");
                last["content"] = (last["content"]?.GetValue<string>() ?? "") + "\n\n（新しい指示により中断しました。）";
            }
            return;
        }
        session.Messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = "（新しい指示により中断しました。）" });
    }
}
