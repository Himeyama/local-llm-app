using System.Text.Json.Nodes;

namespace LocalLlm.Gui.Services;

public static class ChatToolDisplay
{
    public static bool IsToolTurn(JsonObject message) => message["role"]?.GetValue<string>() == "tool" ||
        (message["role"]?.GetValue<string>() == "assistant" && message["tool_calls"] is JsonArray { Count: > 0 });

    public static string Activity(string name, JsonObject arguments)
    {
        string? Value(string key) => arguments[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        var target = name switch
        {
            "execute_command" => Value("command"),
            "list_directory" or "read_file" or "write_file" or "edit_file" => Value("path"),
            "web_search" when Value("query") is { Length: > 0 } query => "https://search.yahoo.co.jp/search?p=" + Uri.EscapeDataString(query.Trim()).Replace("%20", "+").Replace("~", "%7E").Replace("%2A", "*") + "&ei=UTF-8",
            "web_read" => Value("url"),
            "browser" => Value("url") ?? Value("value"),
            _ => null
        };
        if (string.IsNullOrWhiteSpace(target)) return "実行中…: " + name;
        target = string.Join(" ", target.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return target + (name switch { "web_search" => " を検索中…", "list_directory" or "read_file" => " を読み込み中…", "write_file" or "edit_file" => " を書き込み中…", _ => " を実行中…" });
    }
}
