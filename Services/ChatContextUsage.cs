using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public static class ChatContextUsage
{
    private static int? Count(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var count) && count >= 0 ? count : null;

    public static int? Maximum(JsonNode? props)
    {
        var count = Count((props as JsonObject)?["default_generation_settings"] is JsonObject settings ? settings["n_ctx"] : null);
        return count is > 0 ? count : null;
    }

    public static int? Total(JsonNode? usage)
    {
        if (usage is not JsonObject data) return null;
        if (Count(data["total_tokens"]) is { } total) return total;
        if (Count(data["prompt_tokens"]) is { } prompt && Count(data["completion_tokens"]) is { } completion && (long)prompt + completion <= int.MaxValue)
            return prompt + completion;
        return null;
    }

    public static string ReasoningLabel(string effort) => effort switch { "none" => "オフ", "low" => "低", "medium" => "中", "xhigh" => "最高", _ => "—" };
}
