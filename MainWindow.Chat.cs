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
    private bool handlingChatCommand;
    private CancellationTokenSource? chatCommandCancellation;
    private bool selectingSession;
    private bool followChat = true;
    private ContentControl? streamingText;
    private ChatProgress? streamingProgress;
    private const string ChatSpinnerFrames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private int chatSpinnerFrame;
    private readonly DispatcherTimer chatSpinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    private void InitializeChat()
    {
        chatSpinnerTimer.Tick += (_, _) => { chatSpinnerFrame = (chatSpinnerFrame + 1) % ChatSpinnerFrames.Length; ChatActivitySpinner.Text = ChatSpinnerFrames[chatSpinnerFrame].ToString(); };
        try { chatSessions.AddRange(chatStore.Load()); }
        catch (Exception e) { ShowError("履歴を読み込めません: " + e.Message); }
        currentChat = chatSessions.FirstOrDefault();
        if (currentChat == null) CreateChat();
        else { RefreshSessions(); RenderConversation(); }
        Closed += (_, _) => { chatSpinnerTimer.Stop(); chatCancellation?.Cancel(); chatHttp.Dispose(); };
    }

    private void RefreshSessions()
    {
        selectingSession = true;
        SessionList.ItemsSource = null;
        SessionList.ItemsSource = chatSessions.OrderByDescending(s => s.UpdatedAt).ToList();
        SessionList.SelectedItem = currentChat;
        selectingSession = false;
    }

    private void CreateChat()
    {
        if (currentChat != null) drafts[currentChat.Id] = ChatInput.Text;
        currentChat = new ChatSession { Workspace = currentChat?.Workspace ?? settings.RepositoryRoot };
        chatSessions.Add(currentChat);
        try { chatStore.Save(currentChat); } catch (Exception ex) { ShowError("チャットを保存できません: " + ex.Message); }
        attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed; ChatInput.Text = "";
        SetChatActivity("");
        RefreshSessions(); RenderConversation();
        page = "chat"; BuildPage();
    }

    private void OnNewChat(object sender, RoutedEventArgs e) { if (chatCancellation == null && !handlingChatCommand) CreateChat(); }

    private void OnSessionContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (chatCancellation != null) return;
        var source = args.OriginalSource as DependencyObject;
        while (source != null && source != SessionList)
        {
            if (source is FrameworkElement { DataContext: ChatSession session } target)
            {
                ShowSessionContextMenu(session, target, args.TryGetPosition(SessionList, out var position) ? position : null);
                args.Handled = true;
                return;
            }
            source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
        }
        if (!args.TryGetPosition(SessionList, out _) && SessionList.SelectedItem is ChatSession selected)
        {
            ShowSessionContextMenu(selected, SessionList.ContainerFromItem(selected) as FrameworkElement ?? SessionList, null);
            args.Handled = true;
        }
    }

    private void OnHistoryPreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.F10 || chatCancellation != null || SessionList.SelectedItem is not ChatSession session ||
            !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        args.Handled = true;
        ShowSessionContextMenu(session, SessionList.ContainerFromItem(session) as FrameworkElement ?? SessionList, null);
    }

    private void ShowSessionContextMenu(ChatSession session, FrameworkElement target, Windows.Foundation.Point? position)
    {
        var menu = new MenuFlyout();
        var delete = new MenuFlyoutItem { Text = "削除", Icon = new SymbolIcon(Symbol.Delete) };
        delete.Click += async (_, _) => await DeleteChatAsync(session);
        menu.Items.Add(delete);
        if (position.HasValue) menu.ShowAt(SessionList, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = position.Value });
        else menu.ShowAt(target);
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
            chatSessions.Remove(session); drafts.Remove(session.Id); pendingMessages.Remove(session.Id);
            if (currentChat == session)
            {
                currentChat = chatSessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
                attachments.Clear(); AttachmentList.Children.Clear(); AttachmentList.Visibility = Visibility.Collapsed;
                ChatInput.Text = currentChat == null ? "" : drafts.GetValueOrDefault(currentChat.Id, "");
                followChat = true; SetChatActivity("");
                if (currentChat == null) { CreateChat(); return; }
                RenderConversation(); BuildPage();
            }
            RefreshSessions();
        }
        catch (Exception ex) { if (!closing) ShowError("チャットを削除できません: " + ex.Message); }
    }

    private void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (selectingSession || chatCancellation != null || handlingChatCommand || SessionList.SelectedItem is not ChatSession session) return;
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
        if (currentChat == null) return;
        bool busy = chatCancellation != null || handlingChatCommand;
        SendChatButton.IsEnabled = serverStatus.IsConnected || ChatCommands.Parse(ChatInput.Text) == "clear";
        StopChatButton.IsEnabled = chatCancellation != null || chatCommandCancellation != null;
        SendChatButton.Visibility = Visibility.Visible;
        StopChatButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        NewChatButton.IsEnabled = !busy;
        SessionList.IsEnabled = !busy;
        ChatWorkspaceButton.IsEnabled = !busy;
        ChatToolsButton.IsEnabled = !busy;
        ChatReasoningButton.IsEnabled = !busy;
        UpdateReasoningVisibilityButton();
        AttachmentList.IsHitTestVisible = true;
        ChatInput.IsEnabled = true;
        AttachButton.IsEnabled = true;
    }

    private void UpdateChatStatus()
    {
        ChatStatusText.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        var used = currentChat?.ContextTokens ?? (currentChat?.Messages.Count == 0 && currentChat.Summary.Length == 0 ? 0 : (int?)null);
        var percentage = used is >= 0 && maximumChatContext is > 0 ? (100d * used.Value / maximumChatContext.Value).ToString("0.#") : "—";
        ChatStatusText.Text = $"コンテキスト: {used?.ToString("N0") ?? "—"} / {maximumChatContext?.ToString("N0") ?? "—"} トークン ({percentage} %) ｜ 推論: {ChatContextUsage.ReasoningLabel(settings.ChatReasoningEffort)}";
    }

    private void RenderConversation()
    {
        if (closing || currentChat == null) return;
        UpdateChatStatus();
        RenderPendingMessages();
        ToolTipService.SetToolTip(ChatWorkspaceButton, "作業フォルダー設定\n" + currentChat.Workspace);
        ChatMessages.Children.Clear(); streamingText = null;
        if (currentChat.Summary.Length > 0)
            ChatMessages.Children.Add(new Expander { Header = "会話の要約", Content = RenderMarkdown(currentChat.Summary), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        if (currentChat.Messages.Count == 0 && currentChat.Summary.Length == 0)
        {
            ChatMessages.Children.Add(new TextBlock { Text = "何をお手伝いしましょうか？", FontSize = 24, Margin = new Thickness(0, 32, 0, 12) });
            ChatMessages.Children.Add(new TextBlock { Text = "画像について質問したり、ウェブを参照したり、作業フォルダーのファイルを検索・編集できます。", TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
        }
        foreach (var message in currentChat.Messages)
        {
            var role = message["role"]?.GetValue<string>();
            var block = new StackPanel { Spacing = 8 };
            if (role == "assistant" && settings.ChatShowReasoning && message["reasoning_content"]?.GetValue<string>() is { Length: > 0 } reasoning)
                block.Children.Add(RenderReasoning(reasoning));
            if (role == "tool")
            {
                var detail = MessageText(message["content"]?.GetValue<string>() ?? "");
                ChatMessages.Children.Add(new Expander { Header = "ツール: " + message["name"]?.GetValue<string>(), Content = detail, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
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
                if (text.Length > 0) block.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15,
                    Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.White) });
                var bubble = new Border { Child = block, Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    CornerRadius = new CornerRadius(16), Padding = new Thickness(16, 12, 16, 12), HorizontalAlignment = HorizontalAlignment.Right, MaxWidth = ChatScroll.ActualWidth > 0 ? ChatScroll.ActualWidth * 0.8 : 520 };
                ChatMessages.Children.Add(bubble);
            }
            else
            {
                if (text.Length > 0) block.Children.Add(RenderMarkdown(text));
                ChatMessages.Children.Add(block);
            }
        }
        if (chatCancellation != null)
        {
            streamingText = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = RenderStreamingProgress() };
            ChatMessages.Children.Add(streamingText);
        }
        ScrollChatToEnd();
    }

    private static TextBlock RenderReasoning(string text) => new() {
        Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14,
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
        foreach (var bubble in ChatMessages.Children.OfType<Border>()) bubble.MaxWidth = Math.Max(0, e.NewSize.Width * 0.8);
    }

    private void OnChatViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) followChat = ChatScroll.ScrollableHeight - ChatScroll.VerticalOffset < 80;
    }
    private void ScrollChatToEnd()
    {
        if (!followChat) return;
        DispatcherQueue.TryEnqueue(() => { if (!closing && followChat) { ChatScroll.UpdateLayout(); ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true); } });
    }

    private RichTextBlock MessageText(string text)
    {
        var view = new RichTextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 15 };
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

    private async void OnChatWorkspaceSettings(object sender, RoutedEventArgs e)
    {
        if (currentChat == null || chatCancellation != null) return;
        try
        {
            var session = currentChat;
            var input = new TextBox { Header = "作業フォルダー", Text = session.Workspace, MinWidth = 320, TextWrapping = TextWrapping.Wrap };
            var browse = new Button { Content = "フォルダーを選択" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var content = new StackPanel { Spacing = 12 }; content.Children.Add(input); content.Children.Add(browse); content.Children.Add(error);
            browse.Click += async (_, _) => {
                try
                {
                    var picker = new Windows.Storage.Pickers.FolderPicker(); picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                    var folder = await picker.PickSingleFolderAsync();
                    if (folder != null && !closing) { input.Text = folder.Path; error.Visibility = Visibility.Collapsed; }
                }
                catch (Exception ex) { error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "作業フォルダー設定", Content = content,
                PrimaryButtonText = "保存", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) => {
                var previous = session.Workspace;
                try
                {
                    if (string.IsNullOrWhiteSpace(input.Text)) throw new IOException("作業フォルダーを指定してください。");
                    var path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(input.Text.Trim()), settings.RepositoryRoot);
                    if (!Directory.Exists(path)) throw new DirectoryNotFoundException("フォルダーが見つかりません。");
                    session.Workspace = path; chatStore.Save(session);
                    ToolTipService.SetToolTip(ChatWorkspaceButton, "作業フォルダー設定\n" + path);
                }
                catch (Exception ex) { session.Workspace = previous; args.Cancel = true; error.Text = ex.Message; error.Visibility = Visibility.Visible; }
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void OnChatToolsSettings(object sender, RoutedEventArgs e)
    {
        if (chatCancellation != null) return;
        try
        {
            var web = new ToggleSwitch { Header = "ウェブ検索", IsOn = settings.ChatWebEnabled, OnContent = "有効", OffContent = "無効" };
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
            foreach (var (label, value) in new[] { ("オフ", "none"), ("低", "low"), ("中", "medium"), ("最高", "xhigh") })
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

    private async void OnAttachImage(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            foreach (var type in new[] { ".png", ".jpg", ".jpeg", ".webp" }) picker.FileTypeFilter.Add(type);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var files = await picker.PickMultipleFilesAsync();
            foreach (var file in files)
            {
                if (closing) return;
                if (attachments.Count >= 4) throw new IOException("画像は 1 メッセージに 4 枚までです。");
                if (new FileInfo(file.Path).Length > 10 * 1024 * 1024) throw new IOException("画像は 1 枚 10 MiB までです。");
                using (var stream = await file.OpenReadAsync()) { var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); }
                var mime = file.FileType.ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", _ => "image/jpeg" };
                var data = "data:" + mime + ";base64," + Convert.ToBase64String(await File.ReadAllBytesAsync(file.Path));
                var attachment = (file.Name, data); attachments.Add(attachment);
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                var thumbnail = new Image { Width = 48, Height = 48 }; row.Children.Add(thumbnail); _ = LoadThumbnail(thumbnail, data);
                row.Children.Add(new TextBlock { Text = file.Name, VerticalAlignment = VerticalAlignment.Center });
                var remove = new Button { Content = "×" }; ToolTipService.SetToolTip(remove, "画像を取り除く");
                remove.Click += (_, _) => { attachments.Remove(attachment); AttachmentList.Children.Remove(row); if (attachments.Count == 0) AttachmentList.Visibility = Visibility.Collapsed; };
                row.Children.Add(remove); AttachmentList.Children.Add(row); AttachmentList.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { ShowError("画像を追加できません: " + ex.Message); }
    }

    private void SetChatActivity(string text)
    {
        ChatActivity.Text = text;
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
    private void OnStopChat(object sender, RoutedEventArgs e) { chatCommandCancellation?.Cancel(); chatCancellation?.Cancel(); SetChatActivity("停止しています…"); }
    private void OnChatInputGotFocus(object sender, RoutedEventArgs e) => ChatComposer.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private void OnChatInputLostFocus(object sender, RoutedEventArgs e) => ChatComposer.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextControlBorderBrush"];
    private void OnChatKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
        { e.Handled = true; _ = SendChatAsync(); }
    }
    private async void OnSendChat(object sender, RoutedEventArgs e) => await SendChatAsync();

    private async Task SendChatAsync()
    {
        if (closing || currentChat == null) return;
        var text = ChatInput.Text.Trim(); var session = currentChat;
        if (ChatCommands.Parse(text) is { } command) { await RunChatCommandAsync(command, session); return; }
        if (!serverStatus.IsConnected) return;
        if (!pendingMessages.TryGetValue(session.Id, out var pending)) pendingMessages[session.Id] = pending = new();
        bool hasInput = text.Length > 0 || attachments.Count > 0;
        bool retry = !hasInput && session.Messages.LastOrDefault()?["role"]?.GetValue<string>() is "user" or "tool";
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
        } while (!closing && !handlingChatCommand && serverStatus.IsConnected && pending.Count > 0);
    }

    private void OnChatInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) UpdateChatControls();
    }

    private async Task RunChatCommandAsync(string command, ChatSession session)
    {
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
            if (command == "clear") chatCancellation?.Cancel();
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
        ChatSession? generationSession = null;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        activeChatTurn = completion;
        try
        {
            session.ContextTokens = null;
            if (message != null)
            {
                var text = message["content"] is JsonValue value ? value.GetValue<string>() : "";
                session.Messages.Add(message);
                if (session.Title == "新しいチャット") session.Title = text.Length > 0 ? text[..Math.Min(36, text.Length)].Replace('\n', ' ') : "画像についてのチャット";
                try { chatStore.Save(session); }
                catch { session.Messages.Remove(message); throw; }
                pending.Dequeue();
            }
            Notice.IsOpen = false;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30)); chatCancellation = cancellation;
            streamingProgress = null;
            UpdateChatControls(); RenderConversation(); RefreshSessions();
            followChat = true; ScrollChatToEnd();
            PageTitle.Text = session.Title; AppTitleBar.Subtitle = session.Title;
            SetChatActivity("回答を待っています…");
            var progress = new Progress<ChatProgress>(update => {
                if (closing || chatCancellation != cancellation) return;
                if (update.ContextTokens is { } contextTokens) { session.ContextTokens = contextTokens; UpdateChatStatus(); }
                streamingProgress = update;
                if (streamingText != null) streamingText.Content = RenderStreamingProgress();
                SetChatActivity(update.Activity == "完了" ? "" : update.Activity);
                ScrollChatToEnd();
            });
            var tools = new ChatTools(session.Workspace, settings.ChatWebEnabled, settings.ChatWritesEnabled, allowCommands: settings.ChatCommandsEnabled);
            var reasoningEffort = settings.ChatReasoningEffort;
            // Generation owns a separate transcript; the UI receives stable snapshots.
            generationSession = new ChatSession { Id = session.Id, Title = session.Title, Workspace = session.Workspace,
                Summary = session.Summary, Messages = session.Messages.Select(m => (JsonObject)m.DeepClone()).ToList() };
            await Task.Run(() => new ChatClient(chatHttp).GenerateAsync(generationSession, tools, progress, () => {
                chatStore.Save(generationSession);
                var snapshot = generationSession.Messages.Select(m => (JsonObject)m.DeepClone()).ToList();
                var contextTokens = generationSession.ContextTokens;
                DispatcherQueue.TryEnqueue(() => { if (!closing && chatCancellation == cancellation) { session.Messages = snapshot; session.ContextTokens = contextTokens; session.UpdatedAt = generationSession.UpdatedAt; streamingProgress = null; RenderConversation(); } });
            }, cancellation.Token, reasoningEffort));
            cancellation.Token.ThrowIfCancellationRequested();
            SetChatActivity("");
            return true;
        }
        catch (OperationCanceledException) { if (!closing) SetChatActivity("停止しました。保存済みの会話から続けられます。空欄のまま送信すると再試行します。"); }
        catch (Exception ex) { if (!closing) { ShowError(ex.Message); SetChatActivity("応答を取得できませんでした。空欄のまま送信すると再試行します。"); } }
        finally
        {
            if (generationSession != null) { session.Messages = generationSession.Messages; session.ContextTokens = generationSession.ContextTokens; session.UpdatedAt = generationSession.UpdatedAt; }
            chatCancellation = null;
            streamingProgress = null;
            if (!closing) UpdateChatSpinner();
            if (!closing) { UpdateChatControls(); RenderConversation(); RefreshSessions(); }
            activeChatTurn = null; completion.TrySetResult();
        }
        return false;
    }
}
