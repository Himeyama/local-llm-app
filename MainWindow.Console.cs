using LocalLlm.Gui.Controls;
using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TerminalView> consoles = new();
    private string? consoleChatId;
    private readonly HashSet<string> failedConsoleRestores = new();
    private TerminalView? CurrentConsole => currentChat != null ? consoles.GetValueOrDefault(currentChat.Id) : null;

    private async void OnToggleConsole(object sender, RoutedEventArgs e)
    {
        if (currentChat == null) return;
        var session = currentChat;
        var open = ConsolePanel.Visibility != Visibility.Visible || consoleChatId != session.Id;
        session.ConsoleOpen = open;
        try { chatStore.SaveConsoleVisibility(session, open); }
        catch (Exception ex) { ShowError("コンソールの表示状態を保存できません: " + ex.Message); }
        failedConsoleRestores.Remove(session.Id);
        if (!open) { ConsolePanel.Visibility = Visibility.Collapsed; return; }
        await OpenConsoleAsync(session);
    }

    private void RestoreConsoleVisibility()
    {
        if (currentChat?.ConsoleOpen != true) { ConsolePanel.Visibility = Visibility.Collapsed; return; }
        if (!RootLayout.IsLoaded || failedConsoleRestores.Contains(currentChat.Id)) return;
        if (consoleChatId == currentChat.Id && ConsolePanel.Visibility == Visibility.Visible && CurrentConsole is { IsFaulted: false } existing &&
            string.Equals(existing.Directory, Path.GetFullPath(currentChat.Workspace), StringComparison.OrdinalIgnoreCase)) return;
        _ = OpenConsoleAsync(currentChat);
    }

    private async Task OpenConsoleAsync(ChatSession session)
    {
        if (closing || currentChat != session || !session.ConsoleOpen) return;
        var sessionId = session.Id;
        TerminalView? created = null;
        try {
            if (CurrentConsole is { } existing && (existing.IsFaulted || !string.Equals(existing.Directory, Path.GetFullPath(session.Workspace), StringComparison.OrdinalIgnoreCase)))
                RemoveConsole(sessionId);
            if (CurrentConsole == null) {
                created = new TerminalView(session.Workspace);
                created.Failed += error => { if (!closing) ShowError(error); };
                consoles.Add(sessionId, created);
                ConsoleHost.Children.Add(created);
            }
            foreach (var pair in consoles) pair.Value.Visibility = pair.Key == sessionId ? Visibility.Visible : Visibility.Collapsed;
            consoleChatId = sessionId;
            ConsolePanel.Visibility = Visibility.Visible;
            SetConsoleHeight(ConsoleHost.Height);
            if (created != null) await created.InitializeAsync();
            if (!closing && currentChat?.Id == sessionId && ConsolePanel.Visibility == Visibility.Visible) CurrentConsole?.Activate();
        }
        catch (Exception ex) {
            if (created != null && consoles.TryGetValue(sessionId, out var active) && ReferenceEquals(active, created)) RemoveConsole(sessionId);
            if (!closing) {
                failedConsoleRestores.Add(sessionId);
                if (currentChat == session) ConsolePanel.Visibility = Visibility.Collapsed;
                ShowError("コンソールを開けません: " + ex.Message);
            }
        }
    }

    private void RemoveConsole(string sessionId)
    {
        if (!consoles.Remove(sessionId, out var console)) return;
        console.Dispose();
        ConsoleHost.Children.Remove(console);
    }

    private void CloseConsoles()
    {
        foreach (var console in consoles.Values) console.Dispose();
        consoles.Clear();
        ConsoleHost.Children.Clear();
    }
}
