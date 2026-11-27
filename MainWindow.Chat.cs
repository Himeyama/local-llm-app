using System.Text.Json.Nodes;
using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Documents;
using System.Text.RegularExpressions;
using Windows.Storage.Streams;
using LocalLlm.Gui.Controls;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private bool ChatAvailable => settings.ChatProvider == "gemini" || serverStatus.IsConnected;

    private async void OnModelRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        e.Handled = true;
        if (chatCancellation != null || handlingChatCommand) return;
        ComboBox provider = new() { Header = "モデルの種類", HorizontalAlignment = HorizontalAlignment.Stretch };
        provider.Items.Add("ローカル LLM"); provider.Items.Add("Gemini");
        provider.SelectedIndex = settings.ChatProvider == "gemini" ? 1 : 0;
        TextBox url = new() { Header = "URL", Text = settings.GeminiUrl, HorizontalAlignment = HorizontalAlignment.Stretch };
        ComboBox auth = new() { Header = "認証方法", HorizontalAlignment = HorizontalAlignment.Stretch };
        string[] authMethods = ["none", "bearer", "api-key", "x-api-key"];
        foreach (string label in new[] { "認証なし", "Bearer", "api-key", "x-api-key" }) auth.Items.Add(label);
        auth.SelectedIndex = Math.Max(0, Array.IndexOf(authMethods, settings.GeminiAuthMethod));
        PasswordBox key = new() { Header = "キー", Password = settings.GeminiApiKey, HorizontalAlignment = HorizontalAlignment.Stretch };
        void UpdateAuthVisibility() {
            bool gemini = provider.SelectedIndex == 1;
            url.Visibility = auth.Visibility = gemini ? Visibility.Visible : Visibility.Collapsed;
            key.Visibility = gemini && auth.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        provider.SelectionChanged += (_, _) => UpdateAuthVisibility();
        auth.SelectionChanged += (_, _) => UpdateAuthVisibility();
        UpdateAuthVisibility();
        StackPanel content = new() { Spacing = 12 }; content.Children.Add(provider); content.Children.Add(url); content.Children.Add(auth); content.Children.Add(key);
        ContentDialog dialog = new() { XamlRoot = LlamaStatusButton.XamlRoot, Title = "モデル切り替え", Content = content, PrimaryButtonText = "保存", CloseButtonText = "キャンセル" };
        dialog.PrimaryButtonClick += (_, args) => {
            if (provider.SelectedIndex == 1 && (!Uri.TryCreate(url.Text.Trim(), UriKind.Absolute, out Uri? uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))) {
                args.Cancel = true; url.Header = "URL（http または https の絶対 URL を入力してください）"; return;
            }
            if (provider.SelectedIndex == 1 && auth.SelectedIndex != 0 && string.IsNullOrWhiteSpace(key.Password)) {
                args.Cancel = true; key.Header = "キーを入力してください"; return;
            }
            settings.ChatProvider = provider.SelectedIndex == 1 ? "gemini" : "local";
            settings.GeminiUrl = url.Text.Trim(); settings.GeminiAuthMethod = authMethods[auth.SelectedIndex]; settings.GeminiApiKey = key.Password.Trim(); settings.Save(); UpdateStatus();
        };
        await dialog.ShowAsync();
    }
    private readonly ChatStore chatStore = new(Path.Combine(Settings.DataDirectory, "chats"));
    private readonly HttpClient chatHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly List<ChatSession> chatSessions = new();
    private readonly List<(string Name, string Data)> attachments = new();
    private readonly Dictionary<string, string> drafts = new();
    private readonly Dictionary<string, Queue<JsonObject>> pendingMessages = new();
    private ChatSession? currentChat;
    private int? maximumChatContext;
    private CancellationTokenSource? chatCancellation;
    private TaskCompletionSource? activeChatTurn;
    private bool interruptChatTurn;
    private bool handlingChatCommand;
    private CancellationTokenSource? chatCommandCancellation;
    private bool selectingSession;
    private bool followChat = true;
    private bool updatingChatScroll;
    private double lastChatOffset;
    private ContentControl? streamingText;
    private ChatProgress? streamingProgress;
    private const string ChatSpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private int chatSpinnerFrame;
    private readonly DispatcherTimer chatSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeChat()
    {
        ChatScroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) => {
            if (args.GetCurrentPoint(ChatScroll).Properties.MouseWheelDelta > 0) followChat = false;
        }), true);
        SessionList.ContextRequested += OnWorkspaceTreeContext;
        SessionList.Expanding += (_, args) => { if (!selectingSession && args.Node.Content is ChatWorkspace w) { w.Expanded = true; try { settings.Save(); } catch (Exception ex) { ShowError(ex.Message); } } };
        SessionList.Collapsed += (_, args) => { if (!selectingSession && args.Node.Content is ChatWorkspace w) { w.Expanded = false; try { settings.Save(); } catch (Exception ex) { ShowError(ex.Message); } } };
        RootLayout.Loaded += (_, _) => RestoreConsoleVisibility();
        chatSpinnerTimer.Tick += (_, _) => { chatSpinnerFrame = (chatSpinnerFrame + 1) % ChatSpinnerFrames.Length; ChatActivitySpinner.Text = ChatSpinnerFrames[chatSpinnerFrame].ToString(); };
        try { chatSessions.AddRange(chatStore.Load()); }
        catch (Exception e) { ShowError("履歴を読み込めません: " + e.Message); }
        try { MigrateChatWorkspaces(); }
        catch (Exception ex) { ShowError("ワークスペースを読み込めません: " + ex.Message); }
        currentChat = chatSessions.FirstOrDefault(session => session.Id == settings.LastChatId) ?? chatSessions.FirstOrDefault();
        if (settings.ConsoleExpanded == null) {
            settings.ConsoleExpanded = currentChat?.ConsoleOpen ?? false;
            SaveWorkspacePaneLayout();
        }
        RefreshSessions(); RenderConversation();
        RootLayout.ActualThemeChanged += (_, _) => UpdateChatMap();
        Closed += (_, _) => { chatMapReady = false; chatMap?.Close(); chatMap = null; };
        Closed += (_, _) => { chatSpinnerTimer.Stop(); chatCancellation?.Cancel(); chatHttp.Dispose(); CloseConsoles(); };
    }

    private void MigrateChatWorkspaces()
    {
        settings.EnsureDefaultWorkspace();
        foreach (var session in ChatWorkspace.Reconcile(settings.ChatWorkspaces, chatSessions, settings.DefaultWorkspaceDirectory))
            chatStore.Save(session, updateTimestamp: false);
        settings.Save();
    }

    private void RefreshSessions()
    {
        selectingSession = true;
        SessionList.RootNodes.Clear();
        foreach (var workspace in settings.ChatWorkspaces) {
            var root = new TreeViewNode { Content = workspace, IsExpanded = workspace.Expanded };
            foreach (var session in chatSessions.Where(s => s.WorkspaceId == workspace.Id).OrderByDescending(s => s.UpdatedAt)) {
                var child = new TreeViewNode { Content = session };
                root.Children.Add(child);
            }
            SessionList.RootNodes.Add(root);
            if (root.Children.FirstOrDefault(n => ReferenceEquals(n.Content, currentChat)) is { } selected) SessionList.SelectedNode = selected;
        }
        selectingSession = false;
    }

    private void OnSessionInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeViewNode node) return;
        if (node.Content is ChatWorkspace) { node.IsExpanded = !node.IsExpanded; return; }
        if (node.Content is ChatSession session) SelectChat(session);
    }

    private void OnWorkspaceTreeContext(UIElement sender, ContextRequestedEventArgs args)
    {
        if (chatCancellation != null || handlingChatCommand) return;
        var source = args.OriginalSource as DependencyObject;
        while (source != null && source != SessionList) {
            if (source is TreeViewItem item && SessionList.NodeFromContainer(item) is { } node) {
                var menu = new MenuFlyout();
                if (node.Content is ChatWorkspace workspace) {
                    var add = new MenuFlyoutItem { Text = "セッションを追加", Icon = new SymbolIcon(Symbol.Add) };
                    add.Click += (_, _) => Execute(() => CreateChat(workspace));
                    menu.Items.Add(add);
                    var configure = new MenuFlyoutItem { Text = "作業ディレクトリを設定", Icon = new SymbolIcon(Symbol.Folder) };
                    configure.Click += async (_, _) => await ConfigureWorkspaceAsync(workspace);
                    menu.Items.Add(configure);
                    var deleteSessions = new MenuFlyoutItem { Text = "セッションをすべて削除", Icon = new SymbolIcon(Symbol.Delete), IsEnabled = chatSessions.Any(s => s.WorkspaceId == workspace.Id) };
                    deleteSessions.Click += async (_, _) => await DeleteWorkspaceSessionsAsync(workspace);
                    menu.Items.Add(deleteSessions);
                    menu.Items.Add(new MenuFlyoutSeparator());
                    var deleteWorkspace = new MenuFlyoutItem { Text = "削除", Icon = new SymbolIcon(Symbol.Delete) };
                    deleteWorkspace.Click += async (_, _) => await DeleteWorkspaceAsync(workspace);
                    menu.Items.Add(deleteWorkspace);
                }
                else if (node.Content is ChatSession session) {
                    var delete = new MenuFlyoutItem { Text = "削除", Icon = new SymbolIcon(Symbol.Delete) };
                    delete.Click += async (_, _) => await DeleteChatAsync(session);
                    menu.Items.Add(delete);
                }
                menu.ShowAt(item); args.Handled = true; return;
            }
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
    }

    private void OnSessionAddButtonLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) button.Visibility = button.DataContext is TreeViewNode { Content: ChatWorkspace } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSessionAddButtonDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs e)
    {
        sender.Visibility = e.NewValue is TreeViewNode { Content: ChatWorkspace } ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnSessionAddButtonClick(object sender, RoutedEventArgs e)
    {
        if (chatCancellation != null || handlingChatCommand) return;
        if (sender is Button { DataContext: TreeViewNode { Content: ChatWorkspace workspace } }) Execute(() => CreateChat(workspace));
    }

    private async Task DeleteWorkspaceSessionsAsync(ChatWorkspace workspace)
    {
        if (chatCancellation != null || handlingChatCommand || !settings.ChatWorkspaces.Contains(workspace)) return;
        var members = chatSessions.Where(s => s.WorkspaceId == workspace.Id).ToList();
        if (members.Count == 0) return;
        try
        {
            var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "セッションをすべて削除しますか？",
                Content = new TextBlock { Text = $"「{workspace.Name}」の {members.Count} 件のセッションの会話履歴・添付画像を削除します。作業ディレクトリ内のファイルは保持します。", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || chatCancellation != null || handlingChatCommand) return;
            foreach (var session in members)
            {
                chatStore.Delete(session);
                RemoveConsole(session.Id);
                chatSessions.Remove(session); drafts.Remove(session.Id); pendingMessages.Remove(session.Id);
            }
        }
        catch (Exception ex) { if (!closing) ShowError("セッションを削除できません: " + ex.Message); }
        finally
        {
            if (!closing)
            {
                if (currentChat != null && !chatSessions.Contains(currentChat))
                {
                    currentChat = chatSessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
                    attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
                    ChatInput.Text = currentChat == null ? "" : drafts.GetValueOrDefault(currentChat.Id, "");
                    followChat = true; SetChatActivity(""); RenderConversation(); BuildPage();
                }
                RefreshSessions();
            }
        }
    }

    private void CreateChat(ChatWorkspace workspace)
    {
        if (currentChat != null) drafts[currentChat.Id] = ChatInput.Text;
        workspace.Expanded = true;
        settings.Save();
        var created = new ChatSession { Workspace = workspace.Directory, WorkspaceId = workspace.Id };
        chatStore.Save(created);
        currentChat = created;
        chatSessions.Add(created);
        attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed; ChatInput.Text = "";
        SetChatActivity("");
        RefreshSessions(); RenderConversation();
        page = "chat"; BuildPage();
    }

    private async void OnNewChat(object sender, RoutedEventArgs e)
    {
        if (chatCancellation != null || handlingChatCommand) return;
        await ConfigureWorkspaceAsync(null);
    }

    private async Task DeleteWorkspaceAsync(ChatWorkspace workspace)
    {
        if (chatCancellation != null || handlingChatCommand || !settings.ChatWorkspaces.Contains(workspace)) return;
        try {
            var members = chatSessions.Where(s => s.WorkspaceId == workspace.Id).ToList();
            var dialog = new ContentDialog {
                XamlRoot = RootLayout.XamlRoot, Title = "ワークスペースを削除しますか？",
                Content = new TextBlock { Text = $"「{workspace.Name}」と所属する {members.Count} 件のセッションの会話履歴・添付画像を削除します。\n作業ディレクトリ内のファイルは保持します。\n{workspace.Directory}", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || chatCancellation != null || handlingChatCommand) return;
            foreach (var session in members) {
                chatStore.Delete(session);
                RemoveConsole(session.Id);
                chatSessions.Remove(session); drafts.Remove(session.Id); pendingMessages.Remove(session.Id);
            }
            var index = settings.ChatWorkspaces.IndexOf(workspace);
            settings.ChatWorkspaces.Remove(workspace);
            try { settings.Save(); }
            catch { settings.ChatWorkspaces.Insert(index, workspace); throw; }
        }
        catch (Exception ex) { if (!closing) ShowError("ワークスペースを削除できません: " + ex.Message); }
        finally {
            if (!closing) {
                if (currentChat != null && !chatSessions.Contains(currentChat)) {
                    currentChat = chatSessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
                    attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
                    ChatInput.Text = currentChat == null ? "" : drafts.GetValueOrDefault(currentChat.Id, "");
                    followChat = true; SetChatActivity("");
                    RenderConversation(); BuildPage();
                }
                RefreshSessions();
            }
        }
    }

    private async Task DeleteChatAsync(ChatSession session)
    {
        if (chatCancellation != null || handlingChatCommand || !chatSessions.Contains(session)) return;
        try
        {
            var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "チャットを削除しますか？",
                Content = new TextBlock { Text = $"「{session.Title}」の会話履歴と添付画像を削除します。", TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = "削除", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || closing || chatCancellation != null || handlingChatCommand) return;
            chatStore.Delete(session);
            RemoveConsole(session.Id);
            chatSessions.Remove(session); drafts.Remove(session.Id); pendingMessages.Remove(session.Id);
            if (currentChat == session)
            {
                currentChat = chatSessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
                attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
                ChatInput.Text = currentChat == null ? "" : drafts.GetValueOrDefault(currentChat.Id, "");
                followChat = true; SetChatActivity("");
                RenderConversation(); BuildPage();
            }
            RefreshSessions();
        }
        catch (Exception ex) { if (!closing) ShowError("チャットを削除できません: " + ex.Message); }
    }

    private void SelectChat(ChatSession session)
    {
        if (selectingSession || chatCancellation != null || handlingChatCommand) return;
        if (currentChat != null) drafts[currentChat.Id] = ChatInput.Text;
        currentChat = session; ChatInput.Text = drafts.GetValueOrDefault(session.Id, "");
        followChat = true;
        SetChatActivity("");
        attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
        page = "chat"; RenderConversation(); BuildPage();
    }

    private void UpdateChatControls()
    {
        UpdateChatStatus();
        UpdateGitControls();
        if (currentChat == null) {
            SendChatButton.IsEnabled = StopChatButton.IsEnabled = ChatInput.IsEnabled = AttachButton.IsEnabled = false;
            NewChatButton.IsEnabled = SessionList.IsEnabled = true;
            return;
        }
        bool busy = chatCancellation != null || handlingChatCommand;
        SendChatButton.IsEnabled = !gitBusy && (ChatAvailable || ChatCommands.Parse(ChatInput.Text) == "clear");
        StopChatButton.IsEnabled = chatCancellation != null || chatCommandCancellation != null;
        SendChatButton.Visibility = Visibility.Visible;
        StopChatButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.IsEnabled = !busy;
        SessionList.IsEnabled = !busy;
        ChatToolsButton.IsEnabled = !busy;
        ChatReasoningButton.IsEnabled = !busy;
        UpdateReasoningVisibilityButton();
        AttachmentList.IsHitTestVisible = true;
        ChatInput.IsEnabled = true;
        AttachButton.IsEnabled = settings.ChatProvider == "gemini" || (serverStatus.IsConnected && localVisionEnabled);
        ToolTipService.SetToolTip(AttachButton, AttachButton.IsEnabled ? "画像を追加" : "画像入力には対応する mmproj が必要です。");
    }

    private void UpdateChatStatus()
    {
        if (settings.ChatProvider == "gemini") { ChatStatusText.Text = $"Gemini ｜ トークン: {currentChat?.ContextTokens?.ToString("N0") ?? "—"}"; return; }
        ChatStatusText.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        var used = currentChat?.ContextTokens ?? (currentChat?.Messages.Count == 0 && currentChat.Summary.Length == 0 ? 0 : (int?)null);
        var percentage = used is >= 0 && maximumChatContext is > 0 ? (100d * used.Value / maximumChatContext.Value).ToString("0.#") : "—";
        ChatStatusText.Text = $"コンテキスト: {used?.ToString("N0") ?? "—"} / {maximumChatContext?.ToString("N0") ?? "—"} トークン ({percentage} %) ｜ 推論: {ChatContextUsage.ReasoningLabel(settings.ChatReasoningEffort)}";
    }

    private void RenderConversation()
    {
        if (closing) return;
        UpdateChatMap();
        var previousOffset = ChatScroll.VerticalOffset;
        updatingChatScroll = true;
        try { RenderConversationContent(); }
        finally {
            // Rebuilding tool results must not reset the reader's position or resume following.
            ChatScroll.UpdateLayout();
            if (!followChat) ChatScroll.ChangeView(null, Math.Min(previousOffset, ChatScroll.ScrollableHeight), null, true);
            lastChatOffset = ChatScroll.VerticalOffset;
            updatingChatScroll = false;
        }
    }

    private void RenderConversationContent()
    {
        if (currentChat == null) {
            ChatMessages.Children.Clear();
            ChatMessages.Children.Add(new TextBlock { Text = "ワークスペースを右クリックして「セッションを追加」、または「新しいチャット」からワークスペースを作成してください。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 32, 0, 12) });
            RefreshWorkspaceExplorer(); RestoreConsoleVisibility(); UpdateChatControls();
            return;
        }
        if (settings.LastChatId != currentChat.Id) {
            settings.LastChatId = currentChat.Id;
            try { settings.Save(); } catch (Exception ex) { ShowError("開いているチャットを記憶できません: " + ex.Message); }
        }
        UpdateChatStatus();
        RefreshWorkspaceExplorer();
        RestoreConsoleVisibility();
        RenderPendingMessages();
        ChatMessages.Children.Clear(); streamingText = null;
        if (currentChat.Summary.Length > 0)
            ChatMessages.Children.Add(new Expander { Header = "会話の要約", Content = RenderMarkdown(currentChat.Summary), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        if (currentChat.Messages.Count == 0 && currentChat.Summary.Length == 0)
        {
            ChatMessages.Children.Add(new TextBlock { Text = "何をお手伝いしましょうか？", FontSize = 24, Margin = new Thickness(0, 32, 0, 12) });
            ChatMessages.Children.Add(new TextBlock { Text = "画像について質問したり、ウェブを参照したり、作業フォルダーのファイルを検索・編集できます。", TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
        }
        var toolItems = new List<UIElement>();
        var toolLabels = new List<string>();
        void FlushToolItems()
        {
            if (toolItems.Count == 0) return;
            if (toolLabels.Count < 2) { foreach (var item in toolItems) ChatMessages.Children.Add(item); }
            else
            {
                var label = string.Join(" ", toolLabels.Distinct());
                var items = new StackPanel { Spacing = 4, Margin = new Thickness(12, 0, 0, 0) };
                foreach (var item in toolItems) items.Children.Add(item);
                var content = new Border { BorderThickness = new Thickness(1, 0, 0, 0), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Child = items };
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new FontIcon { Glyph = toolLabels.All(label => label == "検索しました") ? "\uE721" : "\uE756", FontSize = 14 });
                header.Children.Add(new TextBlock { Text = label, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Noto Sans JP") });
                var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10 };
                header.Children.Add(chevron);
                var toggle = new Button { Content = header, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4), MinWidth = 0, MinHeight = 0, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Left };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label + " の詳細を表示");
                content.Visibility = Visibility.Collapsed;
                toggle.Click += (_, _) => { var expanded = content.Visibility != Visibility.Visible; content.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; chevron.Glyph = expanded ? "\uE70D" : "\uE76C"; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label + (expanded ? " の詳細を閉じる" : " の詳細を表示")); };
                var group = new StackPanel { Spacing = 4 };
                group.Children.Add(toggle); group.Children.Add(content); ChatMessages.Children.Add(group);
            }
            toolItems.Clear(); toolLabels.Clear();
        }
        foreach (var message in currentChat.Messages)
        {
            var role = message["role"]?.GetValue<string>();
            var toolTurn = ChatToolDisplay.IsToolTurn(message);
            if (!toolTurn) FlushToolItems();
            var block = new StackPanel { Spacing = 8 };
            if (role == "assistant" && settings.ChatShowReasoning && message["reasoning_content"]?.GetValue<string>() is { Length: > 0 } reasoning)
                block.Children.Add(RenderReasoning(reasoning));
            if (role == "tool")
            {
                var result = message["content"]?.GetValue<string>() ?? "";
                if (message["name"]?.GetValue<string>() == "execute_command") result = ChatCommandExecution.FormatForDisplay(result);
                if (message["name"]?.GetValue<string>() == "web_search") result = ChatBrowserSearch.FormatForDisplay(result);
                var detail = MessageText(result);
                var toolName = message["name"]?.GetValue<string>() ?? "";
                (string toolLabel, string toolGlyph) = toolName switch
                {
                    "list_directory" => ("ファイル一覧の取得", "\uE8B7"),
                    "read_file" => ("ファイル読み込み", "\uE8C3"),
                    "write_file" => ("ファイル書き込み", "\uE70F"),
                    "execute_command" => ("コマンドを実行しました", "\uE756"),
                    "web_search" => ("検索しました", "\uE721"),
                    _ => ("ツール: " + toolName, "\uE756")
                };
                var toolPanel = new StackPanel { Spacing = 4 };
                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new FontIcon { Glyph = toolGlyph, FontSize = 14 });
                header.Children.Add(new TextBlock { Text = toolLabel, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Noto Sans JP") });
                var chevron = new FontIcon { Glyph = "\uE76C", FontSize = 10 };
                header.Children.Add(chevron);
                var toggle = new Button { Content = header, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0, 4, 0, 4), MinWidth = 0, MinHeight = 0, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], HorizontalAlignment = HorizontalAlignment.Left };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, toolLabel + " の詳細を表示");
                detail.Visibility = Visibility.Collapsed;
                toggle.Click += (_, _) => { var expanded = detail.Visibility != Visibility.Visible; detail.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed; chevron.Glyph = expanded ? "\uE70D" : "\uE76C"; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, toolLabel + (expanded ? " の詳細を閉じる" : " の詳細を表示")); };
                toolPanel.Children.Add(toggle); toolPanel.Children.Add(detail); toolItems.Add(toolPanel); toolLabels.Add(toolLabel);
                continue;
            }
            var text = "";
            if (message["content"] is JsonValue value) text = value.GetValue<string>();
            else if (message["content"] is JsonArray parts)
            {
                foreach (var part in parts)
                {
                    if (part?["type"]?.GetValue<string>() == "text") text += part["text"]?.GetValue<string>();
                    else if (part?["type"]?.GetValue<string>() == "image_url")
                    {
                        var image = new Image { MaxHeight = 200, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
                        block.Children.Add(image); _ = LoadThumbnail(image, part["image_url"]!["url"]!.GetValue<string>());
                    }
                }
            }
            if (text.Length == 0 && block.Children.Count == 0) continue;
            if (role == "user")
            {
                if (text.Length > 0) block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Noto Sans JP") });
                var bubble = new SquircleBubble(block, (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"])
                    { HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = ChatScroll.ActualWidth > 0 ? ChatScroll.ActualWidth * 0.8 : 520 };
                ChatMessages.Children.Add(bubble);
            }
            else
            {
                if (text.Length > 0) block.Children.Add(RenderMarkdown(text));
                if (role == "assistant" && text.Length > 0) block.Children.Add(CreateMessageActions(text, message["display_timestamp"]?.GetValue<string>()));
                if (toolTurn) toolItems.Add(block); else ChatMessages.Children.Add(block);
            }
        }
        FlushToolItems();
        if (chatCancellation != null)
        {
            streamingText = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = RenderStreamingProgress() };
            ChatMessages.Children.Add(streamingText);
        }
        else if (currentChat.Messages.LastOrDefault() is { } last && last["role"]?.GetValue<string>() == "assistant" && last["display_elapsed_seconds"]?.GetValue<long>() is long seconds && seconds >= 0)
        {
            ChatMessages.Children.Add(new TextBlock { Text = $"作業時間: {seconds / 60:00}:{seconds % 60:00}", FontSize = 12, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Noto Sans JP"), Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        }
        ScrollChatToEnd();
    }

    private static UIElement CreateMessageActions(string markdown, string? timestamp)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(4), MinWidth = 0, MinHeight = 0, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
        var copied = new TextBlock { Text = "コピーしました", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], Visibility = Visibility.Collapsed };
        var copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        copiedTimer.Tick += (_, _) => { copiedTimer.Stop(); copied.Visibility = Visibility.Collapsed; };
        row.Unloaded += (_, _) => copiedTimer.Stop();
        ToolTipService.SetToolTip(copy, "Markdown をコピー");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, "Markdown をコピー");
        copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(markdown); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); copied.Visibility = Visibility.Visible; copiedTimer.Stop(); copiedTimer.Start(); };
        row.Children.Add(copy);
        row.Children.Add(copied);
        if (DateTimeOffset.TryParse(timestamp, out var time)) row.Children.Add(new TextBlock { Text = time.ToLocalTime().ToString("HH:mm"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        return row;
    }

    private static TextBlock RenderReasoning(string text) => new() {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Noto Sans JP"),
        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
    };

    private StackPanel RenderStreamingProgress()
    {
        var block = new StackPanel { Spacing = 8 };
        if (streamingProgress is { } update)
        {
            if (settings.ChatShowReasoning && update.Reasoning.Length > 0) block.Children.Add(RenderReasoning(update.Reasoning));
            if (update.Text.Length > 0) block.Children.Add(RenderMarkdown(update.Text));
        }
        return block;
    }

    private void UpdateReasoningVisibilityButton()
    {
        ChatReasoningVisibilityButton.IsChecked = settings.ChatShowReasoning;
        var label = settings.ChatShowReasoning ? "推論内容を非表示にする" : "推論内容を表示する";
        ToolTipService.SetToolTip(ChatReasoningVisibilityButton, label);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ChatReasoningVisibilityButton, label);
    }

    private void OnToggleChatReasoningVisibility(object sender, RoutedEventArgs e)
    {
        var previous = settings.ChatShowReasoning;
        try { settings.ChatShowReasoning = ChatReasoningVisibilityButton.IsChecked == true; settings.Save(); }
        catch (Exception ex) { settings.ChatShowReasoning = previous; ShowError(ex.Message); }
        UpdateReasoningVisibilityButton(); RenderConversation();
    }

    private StackPanel RenderMarkdown(string text) => new MarkdownView(uri => Execute(() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose())).Render(text);

    private void OnChatSizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var bubble in ChatMessages.Children.OfType<SquircleBubble>()) bubble.MaxWidth = Math.Max(0, e.NewSize.Width * 0.8);
    }

    private void OnChatViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        var offset = ChatScroll.VerticalOffset;
        if (!updatingChatScroll) {
            // React during the gesture, before another streamed update can pull it back down.
            if (offset < lastChatOffset - 0.5) followChat = false;
            else if (offset > lastChatOffset + 0.5 && ChatScroll.ScrollableHeight - offset <= 2) followChat = true;
        }
        lastChatOffset = offset;
    }
    private void ScrollChatToEnd()
    {
        if (!followChat) return;
        DispatcherQueue.TryEnqueue(() => {
            if (closing || !followChat) return;
            ChatScroll.UpdateLayout();
            if (!followChat) return;
            updatingChatScroll = true;
            try { ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true); lastChatOffset = ChatScroll.VerticalOffset; }
            finally { updatingChatScroll = false; }
        });
    }

    private RichTextBlock MessageText(string text)
    {
        var view = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Code, Noto Sans JP") };
        var paragraph = new Paragraph(); int start = 0;
        foreach (Match match in Regex.Matches(text, @"https?://[^\s<>\""\)\]]+", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            paragraph.Inlines.Add(new Run { Text = text[start..match.Index] });
            var url = match.Value.TrimEnd('.', ',', '。', '、');
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var link = new Hyperlink(); link.Inlines.Add(new Run { Text = url });
                link.Click += (_, _) => Execute(() => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose());
                paragraph.Inlines.Add(link);
                paragraph.Inlines.Add(new Run { Text = match.Value[url.Length..] });
            }
            else paragraph.Inlines.Add(new Run { Text = match.Value });
            start = match.Index + match.Length;
        }
        paragraph.Inlines.Add(new Run { Text = text[start..] }); view.Blocks.Add(paragraph); return view;
    }

    private static async Task LoadThumbnail(Image image, string data)
    {
        try
        {
            var bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); }
            stream.Seek(0); var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); image.Source = bitmap;
        }
        catch { image.Visibility = Visibility.Collapsed; }
    }

    private Task ConfigureWorkspaceAsync(ChatWorkspace? workspace)
    {
        if (chatCancellation != null || handlingChatCommand) return Task.CompletedTask;
        ShowFileBrowser(workspace?.Directory ?? settings.DefaultWorkspaceDirectory, true, false, Array.Empty<string>(), paths => {
                try
                {
                    var path = paths[0];
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException("フォルダーが見つかりません。");
                    if (workspace == null) {
                        var created = new ChatWorkspace { Directory = path };
                        settings.ChatWorkspaces.Add(created);
                        try { settings.Save(); }
                        catch { settings.ChatWorkspaces.Remove(created); throw; }
                        CreateChat(created);
                    }
                    else {
                        var previous = workspace.Directory;
                        workspace.Directory = path;
                        try { settings.Save(); }
                        catch { workspace.Directory = previous; throw; }
                        foreach (var member in chatSessions.Where(s => s.WorkspaceId == workspace.Id)) {
                            member.Workspace = path; chatStore.Save(member, updateTimestamp: false);
                        }
                        RefreshSessions();
                    }
                    RefreshWorkspaceExplorer();
                    RestoreConsoleVisibility();
                }
                catch (Exception ex) { ShowError(ex.Message); }
        });
        return Task.CompletedTask;
    }

    private async void OnChatToolsSettings(object sender, RoutedEventArgs e)
    {
        if (chatCancellation != null) return;
        try
        {
            var web = new ToggleSwitch { Header = "ウェブ検索・ブラウザ操作", IsOn = settings.ChatWebEnabled, OnContent = "有効", OffContent = "無効" };
            var writes = new ToggleSwitch { Header = "ファイル変更", IsOn = settings.ChatWritesEnabled, OnContent = "有効", OffContent = "無効" };
            var commands = new ToggleSwitch { Header = "コマンド実行", IsOn = settings.ChatCommandsEnabled, OnContent = "有効", OffContent = "無効" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12, MinWidth = 320 };
            content.Children.Add(web); content.Children.Add(writes); content.Children.Add(commands);
            content.Children.Add(new TextBlock { Text = "コマンドはアプリと同じ権限で実行します。ファイル操作ツールのアクセス制限やバックアップは適用されません。", TextWrapping = TextWrapping.Wrap, MaxWidth = 400, Opacity = 0.65 });
            content.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "ツール設定", Content = content,
                PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) => {
                var oldWeb = settings.ChatWebEnabled; var oldWrites = settings.ChatWritesEnabled; var oldCommands = settings.ChatCommandsEnabled;
                try { settings.ChatWebEnabled = web.IsOn; settings.ChatWritesEnabled = writes.IsOn; settings.ChatCommandsEnabled = commands.IsOn; settings.Save(); }
                catch (Exception ex) { settings.ChatWebEnabled = oldWeb; settings.ChatWritesEnabled = oldWrites; settings.ChatCommandsEnabled = oldCommands; args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnChatReasoningSettings(object sender, RoutedEventArgs e)
    {
        if (chatCancellation != null) return;
        try
        {
            var levels = new ComboBox { Header = "推論レベル", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var (label, value) in new[] { (settings.ChatProvider == "gemini" ? "最小（完全にはオフにできません）" : "オフ", "none"), ("低", "low"), ("中", "medium"), ("最高", "xhigh") })
            {
                var item = new ComboBoxItem { Content = label, Tag = value }; levels.Items.Add(item);
                if (value == settings.ChatReasoningEffort) levels.SelectedItem = item;
            }
            if (levels.SelectedItem == null) levels.SelectedIndex = 3;
            var error = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12, MinWidth = 320 };
            content.Children.Add(levels); content.Children.Add(new TextBlock { Text = "次の送信から適用されます。", Opacity = 0.65 }); content.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "推論設定", Content = content,
                PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) => {
                var previous = settings.ChatReasoningEffort;
                try
                {
                    var item = levels.SelectedItem as ComboBoxItem ?? throw new ArgumentException("推論レベルを選択してください。");
                    settings.ChatReasoningEffort = (string)item.Tag; settings.Save();
                    ToolTipService.SetToolTip(ChatReasoningButton, "推論レベル: " + item.Content);
                    UpdateChatStatus();
                }
                catch (Exception ex) { settings.ChatReasoningEffort = previous; args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { if (!closing) ShowError(ex.Message); }
    }

    private void OnAttachImage(object sender, RoutedEventArgs e)
    {
        ShowFileBrowser(settings.DefaultWorkspaceDirectory, false, true, new[] { ".png", ".jpg", ".jpeg", ".webp" }, paths => _ = AttachImagesAsync(paths));
    }

    private async Task AttachImagesAsync(string[] files)
    {
        try
        {
            foreach (var path in files)
            {
                var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                if (closing) return;
                if (attachments.Count >= 4) throw new IOException("画像は 1 メッセージに 4 枚までです。");
                if (new FileInfo(file.Path).Length > 10 * 1024 * 1024) throw new IOException("画像は 1 枚 10 MiB までです。");
                using (var stream = await file.OpenReadAsync()) { var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); }
                var mime = file.FileType.ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
                var data = "data:" + mime + ";base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(file.Path));
                var attachment = (file.Name, data); attachments.Add(attachment);
                var preview = new Grid { Width = 48, Height = 64 };
                var thumbnail = new Image { Width = 48, Height = 64, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill }; preview.Children.Add(thumbnail); _ = LoadThumbnail(thumbnail, data);
                var remove = new Button { Content = "×", Width = 20, Height = 20, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
                ToolTipService.SetToolTip(remove, "画像を取り除く");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "画像を取り除く");
                remove.Click += (_, _) => { attachments.Remove(attachment); AttachmentList.Children.Remove(preview); if (attachments.Count == 0) AttachmentList.Visibility = Visibility.Collapsed; };
                preview.Children.Add(remove); AttachmentList.Children.Add(preview); AttachmentList.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { ShowError("画像を追加できません: " + ex.Message); }
    }

    private void SetChatActivity(string text, string toolName = "")
    {
        var suffixIndex = toolName.Length > 0 ? text.LastIndexOf(" を", StringComparison.Ordinal) : -1;
        ChatActivity.Text = suffixIndex >= 0 ? text[..suffixIndex] : text;
        ChatActivitySuffix.Text = suffixIndex >= 0 ? text[suffixIndex..] : "";
        ChatActivityToolIcon.Glyph = toolName switch { "web_search" or "web_read" => "\uE721", "read_file" => "\uE8C3", "write_file" or "edit_file" => "\uE70F", _ => "\uE756" };
        ChatActivityToolIcon.Visibility = Visibility.Collapsed;
        ChatActivityPanel.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateChatSpinner();
    }
    private void UpdateChatSpinner()
    {
        if (chatCancellation != null && ChatActivity.Text.Length > 0)
        {
            ChatActivitySpinner.Visibility = Visibility.Visible;
            if (!chatSpinnerTimer.IsEnabled) { chatSpinnerFrame = 0; ChatActivitySpinner.Text = ChatSpinnerFrames[0].ToString(); chatSpinnerTimer.Start(); }
        }
        else { chatSpinnerTimer.Stop(); ChatActivitySpinner.Text = ""; ChatActivitySpinner.Visibility = Visibility.Collapsed; }
    }
    private void OnStopChat(object sender, RoutedEventArgs e) { interruptChatTurn = false; chatCommandCancellation?.Cancel(); chatCancellation?.Cancel(); SetChatActivity("停止しています…"); }
    private void OnChatInputGotFocus(object sender, RoutedEventArgs e) => ChatComposerOutline.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private void OnChatInputLostFocus(object sender, RoutedEventArgs e) => ChatComposerOutline.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextControlBorderBrush"];
    private void OnChatKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { e.Handled = true; _ = SendChatAsync(); }
    }
    private async void OnSendChat(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        if (closing || gitBusy || currentChat == null) return;
        var text = ChatInput.Text.Trim(); var session = currentChat;
        if (text.Equals("/goal", StringComparison.OrdinalIgnoreCase)) { ShowError("/goal の後に目標を入力してください。"); return; }
        if (ChatCommands.Parse(text) is { } command) { await RunChatCommandAsync(command, session); return; }
        if (!ChatAvailable) return;
        if (attachments.Count > 0 && settings.ChatProvider != "gemini" && !localVisionEnabled) { ShowError("画像入力は無効です。画像を取り除くか、対応する mmproj を指定してサーバーを再起動してください。"); return; }
        if (settings.ChatProvider != "gemini" && !localVisionEnabled && session.Messages.Any(m => m["content"] is JsonArray parts && parts.Any(p => p?["type"]?.GetValue<string>() == "image_url")))
        { ShowError("この会話には画像が含まれています。画像入力が無効な場合は新しいチャットを開始してください。"); return; }
        if (!pendingMessages.TryGetValue(session.Id, out var pending)) pendingMessages[session.Id] = pending = new();
        bool hasInput = text.Length > 0 || attachments.Count > 0;
        bool retry = !hasInput && (session.Messages.LastOrDefault()?["role"]?.GetValue<string>() is "user" or "tool" || session.Messages.LastOrDefault()?["incomplete"]?.GetValue<bool>() == true || ChatGoal.HasPendingReview(session));
        if (hasInput)
        {
            JsonNode content = JsonValue.Create(text)!;
            if (attachments.Count > 0)
            {
                var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text.Length > 0 ? text : "この画像について説明してください。" } };
                foreach (var attachment in attachments) parts.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = attachment.Data } });
                content = parts;
            }
            pending.Enqueue(new JsonObject { ["role"] = "user", ["content"] = content });
            ChatInput.Text = ""; drafts.Remove(session.Id); attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
            RenderPendingMessages();
            ChatInput.Focus(FocusState.Programmatic);
            if (activeChatTurn != null && chatCancellation != null && !handlingChatCommand && !chatCancellation.IsCancellationRequested)
            {
                interruptChatTurn = true;
                chatCancellation.Cancel();
                SetChatActivity("新しい指示のために作業を中断しています…");
            }
        }
        if (chatCancellation != null || handlingChatCommand || (!retry && pending.Count == 0)) return;
        await DrainChatQueueAsync(session, pending, retry);
    }

    private async Task DrainChatQueueAsync(ChatSession session, Queue<JsonObject> pending, bool retry = false)
    {
        do
        {
            var message = retry ? null : pending.Peek();
            if (!await GenerateChatTurnAsync(session, message, pending)) break;
            retry = false;
        } while (!closing && !handlingChatCommand && ChatAvailable && pending.Count > 0);
    }

    private void OnChatInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) UpdateChatControls();
    }

    private async Task RunChatCommandAsync(string command, ChatSession session)
    {
        if (command == "compress" && settings.ChatProvider == "gemini") { ShowError("Gemini では会話の圧縮に対応していません。"); return; }
        if (handlingChatCommand) { ShowError("チャットコマンドを処理中です。"); return; }
        if (command == "compress" && !serverStatus.IsConnected) { ShowError("会話の圧縮にはサーバーへの接続が必要です。"); return; }
        if (command == "compress" && attachments.Count > 0) { ShowError("圧縮コマンドに画像は添付できません。画像を取り除いてください。"); return; }
        using var commandCancellation = new CancellationTokenSource();
        chatCommandCancellation = commandCancellation;
        handlingChatCommand = true;
        ChatInput.Text = ""; drafts.Remove(session.Id);
        UpdateChatControls();
        bool completed = false;
        try
        {
            var active = activeChatTurn;
            if (command == "clear") { interruptChatTurn = false; chatCancellation?.Cancel(); }
            if (active != null) { SetChatActivity(command == "clear" ? "停止して会話をクリアしています…" : "回答の完了後に会話を圧縮します…"); await active.Task; }
            if (closing) return;
            commandCancellation.Token.ThrowIfCancellationRequested();
            ChatSession replacement;
            if (command == "clear") replacement = ChatCommands.Clear(session);
            else
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(commandCancellation.Token);
                cancellation.CancelAfter(TimeSpan.FromMinutes(30));
                chatCancellation = cancellation;
                UpdateChatControls(); SetChatActivity("会話を圧縮しています…");
                var progress = new Progress<ChatProgress>(_ => { if (!closing && chatCancellation == cancellation) SetChatActivity("会話を圧縮しています…"); });
                replacement = await Task.Run(() => ChatCommands.CompressAsync(session, new ChatClient(chatHttp), progress, cancellation.Token, settings.ChatReasoningEffort));
                cancellation.Token.ThrowIfCancellationRequested();
            }
            // Save the replacement before changing the visible session.
            chatStore.Save(replacement);
            session.Messages = replacement.Messages; session.Summary = replacement.Summary;
            session.CompactedMessageCount = replacement.CompactedMessageCount;
            session.Title = replacement.Title; session.ContextTokens = replacement.ContextTokens; session.UpdatedAt = replacement.UpdatedAt;
            if (command == "clear")
            {
                pendingMessages.Remove(session.Id);
                attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
            }
            SetChatActivity(command == "clear" ? "会話をクリアしました。" : "会話を要約して圧縮しました。");
            completed = true;
        }
        catch (OperationCanceledException) { if (!closing) SetChatActivity("コマンドを停止しました。元の会話を保持しています。"); }
        catch (Exception ex) { if (!closing) { ShowError(ex.Message); SetChatActivity("コマンドを実行できませんでした。元の会話を保持しています。"); } }
        finally
        {
            handlingChatCommand = false; chatCancellation = null; chatCommandCancellation = null;
            if (!closing) { UpdateChatSpinner(); UpdateChatControls(); RenderConversation(); RefreshSessions(); BuildPage(); }
        }
        if (completed && command == "compress" && !closing && serverStatus.IsConnected && pendingMessages.TryGetValue(session.Id, out var pending) && pending.Count > 0)
            await DrainChatQueueAsync(session, pending);
    }

    private void RenderPendingMessages()
    {
        PendingChatMessages.Children.Clear();
        if (currentChat != null && pendingMessages.TryGetValue(currentChat.Id, out var pending))
        {
            foreach (var message in pending)
            {
                var content = message["content"];
                var text = content is JsonArray parts ? string.Join(" ", parts.Select(p => p?["type"]?.GetValue<string>() == "text" ? p["text"]?.GetValue<string>() : "[画像]")) : content?.GetValue<string>() ?? "";
                PendingChatMessages.Children.Add(new TextBlock { Text = "送信待ち: " + text, TextTrimming = TextTrimming.CharacterEllipsis });
            }
        }
        PendingChatScroll.Visibility = PendingChatMessages.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> GenerateChatTurnAsync(ChatSession session, JsonObject? message, Queue<JsonObject> pending)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        ChatSession? generationSession = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeChatTurn = completion;
        try
        {
            if (settings.ChatProvider != "gemini" && !localVisionEnabled &&
                (session.Messages.Append(message).Where(m => m != null).Any(m => m!["content"] is JsonArray parts && parts.Any(p => p?["type"]?.GetValue<string>() == "image_url"))))
                throw new InvalidOperationException("画像入力は無効です。画像を含む会話は送信できません。");
            if (message != null)
            {
                var text = message["content"] is JsonValue value ? value.GetValue<string>() : "";
                session.Messages.Add(message);
                if (session.Title == "新しいチャット") session.Title = text.Length > 0 ? text[..Math.Min(36, text.Length)].Replace('\n', ' ') : "画像についてのチャット";
                try { chatStore.Save(session); }
                catch { session.Messages.Remove(message); throw; }
                pending.Dequeue();
            }
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30)); chatCancellation = cancellation;
            streamingProgress = null;
            UpdateChatControls(); RenderConversation(); RefreshSessions();
            followChat = true; ScrollChatToEnd();
            PageTitle.Text = session.Title;
            SetChatActivity("回答を待っています…");
            var progress = new Progress<ChatProgress>(update => {
                if (closing || chatCancellation != cancellation) return;
                if (update.ContextTokens is { } contextTokens) { session.ContextTokens = contextTokens; UpdateChatStatus(); }
                streamingProgress = update;
                if (streamingText != null) streamingText.Content = RenderStreamingProgress();
                SetChatActivity(update.Activity == "完了" ? "" : update.Activity, update.ToolName);
                ScrollChatToEnd();
            });
            var tools = new ChatTools(session.Workspace, settings.ChatWebEnabled, settings.ChatWritesEnabled, allowCommands: settings.ChatCommandsEnabled, browserTool: async (args, ct) => {
                if (settings.ChatProvider != "gemini" && !localVisionEnabled && args["action"]?.GetValue<string>() == "screenshot")
                    throw new InvalidOperationException("画像入力が無効のため、スクリーンショットをモデルに送信できません。");
                return await FileEditor.ExecuteBrowserAsync(args, ct);
            });
            var reasoningEffort = settings.ChatReasoningEffort;
            // Generation owns a separate transcript; the UI receives stable snapshots.
            generationSession = new ChatSession { Id = session.Id, Title = session.Title, Workspace = session.Workspace, WorkspaceId = session.WorkspaceId,
                Summary = session.Summary, CompactedMessageCount = session.CompactedMessageCount, ContextTokens = session.ContextTokens,
                Messages = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList() };
            Action saveGeneration = () => {
                chatStore.Save(generationSession);
                var snapshot = generationSession.Messages.Select(m => (JsonObject)m.DeepClone()).ToList();
                var contextTokens = generationSession.ContextTokens;
                var summary = generationSession.Summary;
                var compactedCount = generationSession.CompactedMessageCount;
                DispatcherQueue.TryEnqueue(() => { if (!closing && chatCancellation == cancellation) { session.Messages = snapshot; session.Summary = summary; session.CompactedMessageCount = compactedCount; session.ContextTokens = contextTokens; session.UpdatedAt = generationSession.UpdatedAt; streamingProgress = null; RenderConversation(); } });
            };
            string provider = settings.ChatProvider, url = settings.GeminiUrl, authMethod = settings.GeminiAuthMethod, apiKey = settings.GeminiApiKey;
            await Task.Run(async () => {
                async Task Generate(ChatSession target, bool allowTools, IProgress<ChatProgress> output, Action save) {
                    if (provider == "gemini") await new GeminiChatClient(chatHttp, url, authMethod, apiKey).GenerateAsync(target, tools, output, save, cancellation.Token, reasoningEffort, allowTools);
                    else await new ChatClient(chatHttp).GenerateAsync(target, tools, output, save, cancellation.Token, reasoningEffort, allowTools);
                }
                var goal = ChatGoal.FindActive(generationSession);
                if (goal == null) await Generate(generationSession, true, progress, saveGeneration);
                else await ChatGoal.RunAsync(generationSession, goal, Generate, progress, saveGeneration, cancellation.Token);
            });
            cancellation.Token.ThrowIfCancellationRequested();
            if (generationSession.Messages.LastOrDefault() is { } answer && answer["role"]?.GetValue<string>() == "assistant")
            {
                answer["display_elapsed_seconds"] = (long)elapsed.Elapsed.TotalSeconds;
                chatStore.Save(generationSession);
            }
            SetChatActivity("");
            return true;
        }
        catch (OperationCanceledException)
        {
            if (interruptChatTurn && !closing && !handlingChatCommand && generationSession != null)
            {
                try
                {
                    ChatInterruption.CompleteTranscript(generationSession);
                    chatStore.Save(generationSession);
                    SetChatActivity("新しい指示を処理しています…");
                    return true;
                }
                catch (Exception ex) { ShowError(ex.Message); SetChatActivity("会話を保存できませんでした。送信待ちのメッセージは保持しています。"); }
            }
            else if (!closing) SetChatActivity("停止しました。保存済みの会話から続けられます。空欄のまま送信すると再試行します。");
        }
        catch (Exception ex) { if (!closing) { ShowError(ex.Message); SetChatActivity("応答を取得できませんでした。空欄のまま送信すると再試行します。"); } }
        finally
        {
            if (generationSession != null) { session.Messages = generationSession.Messages; session.Summary = generationSession.Summary; session.CompactedMessageCount = generationSession.CompactedMessageCount; session.ContextTokens = generationSession.ContextTokens; session.UpdatedAt = generationSession.UpdatedAt; }
            chatCancellation = null;
            interruptChatTurn = false;
            streamingProgress = null;
            if (!closing) UpdateChatSpinner();
            if (!closing) { UpdateChatControls(); RenderConversation(); RefreshSessions(); }
            activeChatTurn = null; completion.TrySetResult();
        }
        return false;
    }
}
