using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    WebView2? chatMap;
    Task? chatMapInitialization;
    bool chatMapReady;

    async void OnOpenChat3D(object sender, RoutedEventArgs e)
    {
        bool show = ChatMapButton.IsChecked == true;
        if (show) ChatMapButton.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White);
        else ChatMapButton.ClearValue(Control.ForegroundProperty);
        ChatMapHost.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ChatScroll.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        if (!show) return;
        try {
            if (chatMap == null) {
                chatMap = new WebView2();
                ChatMapHost.Children.Add(chatMap);
                chatMapInitialization = InitializeChatMapAsync(chatMap);
            }
            if (chatMapInitialization != null) await chatMapInitialization;
            UpdateChatMap();
        }
        catch (Exception ex) {
            chatMapReady = false;
            if (chatMap != null) { ChatMapHost.Children.Remove(chatMap); chatMap.Close(); chatMap = null; }
            chatMapInitialization = null;
            ChatMapButton.IsChecked = false;
            ChatMapButton.ClearValue(Control.ForegroundProperty);
            ChatMapHost.Visibility = Visibility.Collapsed; ChatScroll.Visibility = Visibility.Visible;
            if (!closing) ShowError("会話ビューを開けません: " + ex.Message);
        }
    }

    async Task InitializeChatMapAsync(WebView2 view)
    {
        await view.EnsureCoreWebView2Async();
        if (closing || view != chatMap) return;
        CoreWebView2 core = view.CoreWebView2;
        core.Settings.AreHostObjectsAllowed = false;
        core.SetVirtualHostNameToFolderMapping("chat3d.local", Path.Combine(AppContext.BaseDirectory, "Chat3D"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.NavigationStarting += (_, args) => { if (args.Uri != "https://chat3d.local/index.html") args.Cancel = true; };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, args) => {
            if (!closing && view == chatMap && args.Source == "https://chat3d.local/index.html" && args.TryGetWebMessageAsString() == "ready") {
                chatMapReady = true; UpdateChatMap();
            }
        };
        core.Navigate("https://chat3d.local/index.html");
    }

    void UpdateChatMap()
    {
        if (!chatMapReady || chatMap == null || ChatMapButton.IsChecked != true) return;
        List<object> entries = [];
        if (currentChat != null) {
            if (!string.IsNullOrEmpty(currentChat.Summary)) entries.Add(new { role = "summary", text = currentChat.Summary });
            foreach (JsonObject message in currentChat.Messages) {
                string role = message["role"]?.GetValue<string>() ?? "assistant";
                string text = message["content"] is JsonArray parts
                    ? string.Join("\n", parts.Select(part => part?["type"]?.GetValue<string>() == "text" ? part?["text"]?.GetValue<string>() : "[画像]").Where(value => !string.IsNullOrEmpty(value)))
                    : message["content"]?.GetValue<string>() ?? "";
                if (!string.IsNullOrWhiteSpace(text) || role == "tool") entries.Add(new { role, text, name = message["name"]?.GetValue<string>() ?? "", toolId = ShortToolId(message["tool_call_id"]?.GetValue<string>()) });
                if (message["tool_calls"] is JsonArray calls)
                    foreach (JsonNode? call in calls)
                        entries.Add(new { role = "call", text = call?["function"]?["arguments"]?.GetValue<string>() ?? "", name = call?["function"]?["name"]?.GetValue<string>() ?? "tool", toolId = ShortToolId(call?["id"]?.GetValue<string>()) });
            }
        }
        string payload = JsonSerializer.Serialize(new { type = "conversation", sessionId = currentChat?.Id ?? "", title = currentChat?.Title ?? "会話", theme = RootLayout.ActualTheme == ElementTheme.Dark ? "dark" : "light", entries });
        chatMap.CoreWebView2.PostWebMessageAsJson(payload);
    }

    static string ShortToolId(string? id) => string.IsNullOrEmpty(id) ? "" : id[^Math.Min(6, id.Length)..];
}
