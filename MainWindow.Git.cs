using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private readonly GitRepository git = new();
    private readonly DispatcherTimer gitTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly CancellationTokenSource gitLifetime = new();
    private readonly Dictionary<string, string> gitDrafts = new(StringComparer.OrdinalIgnoreCase);
    private GitSnapshot? gitSnapshot;
    private string gitWorkspace = "", gitError = "";
    private bool gitBusy;
    private readonly SemaphoreSlim gitReadGate = new(1, 1);
    private long gitRevision;
    private long gitContext;
    private readonly TextBlock gitRepositoryText = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBlock gitTrackingText = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.65 };
    private readonly Grid gitSectionBody = new() { RowSpacing = 6 };
    private readonly Button gitSectionToggle = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Padding = new Thickness(0), MinHeight = 32 };
    private readonly FontIcon gitSectionChevron = new() { Glyph = "\uE70D", FontSize = 10 };
    private readonly ProgressBar gitProgress = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly TextBox gitMessage = new() { PlaceholderText = "コミットメッセージ", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 100 };
    private readonly ListView gitUnstaged = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView gitStaged = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly TextBlock gitUnstagedTitle = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock gitStagedTitle = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly AppBarButton gitFetch = new() { Icon = new SymbolIcon(Symbol.Refresh), Label = "フェッチ・更新" };
    private readonly AppBarButton gitPull = new() { Icon = new SymbolIcon(Symbol.Download), Label = "プル" };
    private readonly AppBarButton gitPush = new() { Icon = new SymbolIcon(Symbol.Upload), Label = "プッシュ" };
    private readonly AppBarButton gitStageAll = new() { Icon = new SymbolIcon(Symbol.Add), Label = "すべてステージ" };
    private readonly AppBarButton gitUnstageAll = new() { Icon = new SymbolIcon(Symbol.Remove), Label = "すべて解除" };
    private readonly Button gitStage = new() { Content = "選択をステージ" };
    private readonly Button gitUnstage = new() { Content = "選択を解除" };
    private readonly Button gitCommit = new() { Content = "コミット", VerticalAlignment = VerticalAlignment.Top };

    private void InitializeGit()
    {
        GitPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        GitPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var heading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        heading.Children.Add(gitSectionChevron); heading.Children.Add(new TextBlock { Text = "Git", FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        gitSectionToggle.Content = heading; gitSectionToggle.Click += OnToggleGitSection;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitSectionToggle, "Git を折りたたむ");
        GitPanel.Children.Add(gitSectionToggle);
        Grid.SetRow(gitSectionBody, 1); GitPanel.Children.Add(gitSectionBody);
        for (int i = 0; i < 4; i++) gitSectionBody.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var commands = new CommandBar { DefaultLabelPosition = CommandBarDefaultLabelPosition.Collapsed, IsDynamicOverflowEnabled = true,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        foreach (var button in new[] { gitFetch, gitPull, gitPush }) commands.PrimaryCommands.Add(button);
        commands.SecondaryCommands.Add(gitStageAll); commands.SecondaryCommands.Add(gitUnstageAll);
        gitSectionBody.Children.Add(commands);
        var status = new StackPanel { Spacing = 4 };
        status.Children.Add(gitProgress);
        Grid.SetRow(status, 3); gitSectionBody.Children.Add(status);
        var commitRow = new Grid { ColumnSpacing = 4 };
        gitMessage.AcceptsReturn = false; gitMessage.TextWrapping = TextWrapping.NoWrap;
        gitCommit.Content = new SymbolIcon(Symbol.Accept); gitCommit.Padding = new Thickness(6); gitCommit.MinWidth = 32;
        ToolTipService.SetToolTip(gitCommit, "ステージした変更をコミット");
        commitRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        commitRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        commitRow.Children.Add(gitMessage); Grid.SetColumn(gitCommit, 1); commitRow.Children.Add(gitCommit);
        Grid.SetRow(commitRow, 1); gitSectionBody.Children.Add(commitRow);
        var lists = new Grid { RowSpacing = 4 };
        gitStage.Content = new SymbolIcon(Symbol.Add); gitUnstage.Content = new SymbolIcon(Symbol.Remove);
        foreach (var button in new[] { gitStage, gitUnstage }) { button.MinWidth = button.MinHeight = 24; button.Padding = new Thickness(4); }
        ToolTipService.SetToolTip(gitStage, "すべての変更をステージ"); ToolTipService.SetToolTip(gitUnstage, "すべてのステージを解除");
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, new GridLength(1, GridUnitType.Star) }) lists.RowDefinitions.Add(new RowDefinition { Height = height });
        void AddHeader(TextBlock title, Button action, int row) {
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
            title.FontSize = 12;
            header.Children.Add(title); Grid.SetColumn(action, 1); header.Children.Add(action); Grid.SetRow(header, row); lists.Children.Add(header);
        }
        AddHeader(gitUnstagedTitle, gitStage, 0); AddHeader(gitStagedTitle, gitUnstage, 2);
        var template = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid Padding="0,6" ColumnSpacing="12"><Grid.ColumnDefinitions><ColumnDefinition Width="56"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions><TextBlock Text="{Binding Status}" FontFamily="Cascadia Code"/><TextBlock Grid.Column="1" Text="{Binding DisplayPath}" TextTrimming="CharacterEllipsis" ToolTipService.ToolTip="{Binding DisplayPath}"/></Grid></DataTemplate>
            """);
        gitUnstaged.ItemTemplate = gitStaged.ItemTemplate = template;
        Grid.SetRow(gitUnstaged, 1); lists.Children.Add(gitUnstaged); Grid.SetRow(gitStaged, 3); lists.Children.Add(gitStaged);
        Grid.SetRow(lists, 2); gitSectionBody.Children.Add(lists);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitMessage, "コミットメッセージ");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitUnstaged, "ステージされていない変更");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitStaged, "ステージした変更");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitCommit, "ステージした変更をコミット");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitStage, "すべての変更をステージ");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitUnstage, "すべてのステージを解除");
        gitMessage.TextChanged += (_, _) => { if (gitSnapshot is { } state) gitDrafts[state.Root] = gitMessage.Text; UpdateGitControls(); };
        gitUnstaged.SelectionChanged += (_, _) => UpdateGitControls(); gitStaged.SelectionChanged += (_, _) => UpdateGitControls();
        gitFetch.Click += async (_, _) => await RunGitOperationAsync("フェッチ", (state, token) => state.Remotes.Count > 0 ? git.FetchAsync(state, token) : Task.FromResult("ローカルの状態を更新しました。"));
        gitPull.Click += OnGitPull; gitPush.Click += OnGitPush;
        gitStageAll.Click += async (_, _) => await RunGitOperationAsync("ステージング", (state, token) => git.StageAsync(state, null, token));
        gitUnstageAll.Click += async (_, _) => await RunGitOperationAsync("ステージ解除", (state, token) => git.UnstageAsync(state, null, token));
        gitStage.Click += async (_, _) => await RunGitOperationAsync("ステージング", (state, token) => git.StageAsync(state, null, token));
        gitUnstage.Click += async (_, _) => await RunGitOperationAsync("ステージ解除", (state, token) => git.UnstageAsync(state, null, token));
        AddGitFileMenu(gitUnstaged, staged: false); AddGitFileMenu(gitStaged, staged: true);
        gitCommit.Click += async (_, _) => {
            var message = gitMessage.Text;
            if (await RunGitOperationAsync("コミット", (state, token) => git.CommitAsync(state, message, token)) && gitMessage.Text == message) gitMessage.Text = "";
        };
        gitTimer.Tick += async (_, _) => await RefreshGitAsync();
        RootLayout.Loaded += (_, _) => { gitTimer.Start(); _ = RefreshGitAsync(); };
        Activated += (_, args) => { if (args.WindowActivationState != WindowActivationState.Deactivated) _ = RefreshGitAsync(); };
        Closed += (_, _) => { gitTimer.Stop(); gitLifetime.Cancel(); };
    }

    private void RefreshGitContext()
    {
        var workspace = currentChat?.Workspace ?? "";
        if (!string.Equals(workspace, gitWorkspace, StringComparison.OrdinalIgnoreCase)) {
            gitWorkspace = workspace; gitContext++; gitSnapshot = null; gitError = "";
            gitMessage.Text = ""; SetGitOperationMessage("");
            UpdateGitView(); _ = RefreshGitAsync();
        }
        UpdateGitControls();
    }

    private async Task RefreshGitAsync(bool afterOperation = false)
    {
        if (closing || (gitBusy && !afterOperation)) return;
        // Mutation refreshes wait for any earlier read, then read the new index themselves.
        // A timer read that started before staging must never replace that result.
        if (!afterOperation && gitReadGate.CurrentCount == 0) return;
        try { await gitReadGate.WaitAsync(gitLifetime.Token); }
        catch (OperationCanceledException) { return; }
        var context = gitContext; var workspace = gitWorkspace; var revision = gitRevision;
        try {
            var state = await git.ReadAsync(workspace, gitLifetime.Token);
            if (closing || context != gitContext || revision != gitRevision) return;
            var oldRoot = gitSnapshot?.Root;
            gitSnapshot = state; gitError = "";
            if (state?.Root != oldRoot) gitMessage.Text = state == null ? "" : gitDrafts.GetValueOrDefault(state.Root, "");
            UpdateGitView();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) {
            if (!closing && context == gitContext && revision == gitRevision) {
                gitSnapshot = null;
                if (gitError != ex.Message) AddLog("Git: " + ex.Message);
                gitError = ex.Message; SetGitOperationMessage("Git の状態を取得できません: " + ex.Message); UpdateGitView();
            }
        }
        finally {
            gitReadGate.Release();
            if (!closing && context != gitContext) _ = RefreshGitAsync();
        }
    }

    private void UpdateGitView()
    {
        var state = gitSnapshot;
        gitRepositoryText.Text = state == null ? gitError : $"{state.Branch}  ·  {state.Root}";
        gitTrackingText.Text = state == null ? "" : state.Upstream != null ?
            $"追跡先: {state.Upstream}  ·  ↓ {state.Behind} / ↑ {state.Ahead}（最後にフェッチした状態）" :
            state.Detached ? "HEAD がブランチから切り離されています。新規ブランチを作成できます。" :
            state.CanPush ? $"追跡先なし。プッシュ時に {state.PushRemote} に同名ブランチを作成します。" : "追跡先なし。リモートの設定はターミナルで行ってください。";
        var changes = state?.Changes ?? Array.Empty<GitChange>();
        ToolTipService.SetToolTip(GitPanel, gitRepositoryText.Text);
        void SetChanges(ListView list, IEnumerable<GitChange> source) {
            var items = source.ToList();
            if (list.ItemsSource is IEnumerable<GitChange> previous && previous.SequenceEqual(items)) return;
            var selected = (list.SelectedItem as GitChange)?.Path;
            list.ItemsSource = items; list.SelectedItem = items.FirstOrDefault(item => item.Path == selected);
        }
        SetChanges(gitUnstaged, changes.Where(change => change.IsUnstaged)); SetChanges(gitStaged, changes.Where(change => change.IsStaged));
        gitUnstagedTitle.Text = $"変更 ({changes.Count(change => change.IsUnstaged)})";
        gitStagedTitle.Text = $"ステージした変更 ({changes.Count(change => change.IsStaged)})";
        if (state != null) {
            GitBranchText.Text = state.Branch;
            ToolTipService.SetToolTip(GitBranchButton, state.Root + "\nブランチを切り替え・作成");
            GitStatusPullButton.Content = $"↓ {state.Behind}"; GitStatusPushButton.Content = $"↑ {state.Ahead}";
            ToolTipService.SetToolTip(GitStatusPullButton, "追跡先からプル（最後にフェッチした状態）");
            ToolTipService.SetToolTip(GitStatusPushButton, "追跡先へプッシュ");
            gitPull.Label = state.Upstream != null ? $"プル ({state.Behind})" : "プル";
            gitPush.Label = state.Upstream != null ? $"プッシュ ({state.Ahead})" : "プッシュ・追跡先を設定";
            ToolTipService.SetToolTip(gitPull, gitPull.Label + "\n" + gitTrackingText.Text);
            ToolTipService.SetToolTip(gitPush, gitPush.Label + "\n" + gitTrackingText.Text);
        }
        UpdateGitControls();
    }

    private bool GitAvailable => gitSnapshot != null && !gitBusy && chatCancellation == null && !handlingChatCommand;
    private void UpdateGitControls()
    {
        if (GitPanel == null) return;
        var state = gitSnapshot;
        var visibility = state != null ? Visibility.Visible : Visibility.Collapsed;
        if (GitPanel.Visibility != visibility) {
            GitPanel.Visibility = visibility;
            UpdateHistoryLayout();
        }
        GitStatus.Visibility = state != null && page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        GitBranchButton.IsEnabled = GitAvailable;
        GitStatusPullButton.Visibility = state?.CanPull == true && state.Behind > 0 ? Visibility.Visible : Visibility.Collapsed;
        GitStatusPushButton.Visibility = state?.CanPush == true && state.Upstream != null && state.Ahead > 0 ? Visibility.Visible : Visibility.Collapsed;
        GitStatusPullButton.IsEnabled = gitPull.IsEnabled = GitAvailable && state?.CanPull == true;
        GitStatusPushButton.IsEnabled = gitPush.IsEnabled = GitAvailable && state?.CanPush == true;
        gitFetch.IsEnabled = GitAvailable;
        gitStageAll.IsEnabled = GitAvailable && state!.Changes.Any(change => change.IsUnstaged);
        gitUnstageAll.IsEnabled = GitAvailable && state!.Changes.Any(change => change.IsStaged);
        gitStage.IsEnabled = GitAvailable && state!.Changes.Any(change => change.IsUnstaged);
        gitUnstage.IsEnabled = GitAvailable && state!.Changes.Any(change => change.IsStaged);
        gitCommit.IsEnabled = GitAvailable && state!.Changes.Any(change => change.IsStaged) && !state.Changes.Any(change => change.Conflicted) && !string.IsNullOrWhiteSpace(gitMessage.Text);
        gitMessage.IsEnabled = !gitBusy;
        gitProgress.Visibility = gitBusy ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task<bool> RunGitOperationAsync(string label, Func<GitSnapshot, CancellationToken, Task<string>> operation, bool changesFiles = false, GitSnapshot? expected = null)
    {
        if (!GitAvailable || gitSnapshot is not { } state || (expected != null && (expected.Root != state.Root || expected.Branch != state.Branch))) return false;
        if (changesFiles && FileEditor.HasPendingChanges) { SetGitOperationMessage("未保存の編集内容があります。保存またはタブを閉じてから Git 操作を実行してください。"); return false; }
        gitBusy = true; gitRevision++; UpdateGitControls(); UpdateChatControls();
        var context = gitContext;
        if (changesFiles) FileEditor.InteractionEnabled = false;
        SetGitOperationMessage(label + "中…");
        try {
            var output = await operation(state, gitLifetime.Token);
            if (closing) return false;
            AddLog($"Git {label}: {output}");
            if (context == gitContext) SetGitOperationMessage(label + "が完了しました。\n" + output);
            return context == gitContext;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { if (!closing) { if (context == gitContext) SetGitOperationMessage($"Git {label}: {ex.Message}"); AddLog($"Git {label}: {ex.Message}"); } return false; }
        finally {
            if (!closing) {
                if (changesFiles) await FileEditor.RefreshCleanFilesAsync();
                if (changesFiles) FileEditor.InteractionEnabled = true;
                try { await RefreshGitAsync(afterOperation: true); }
                finally { gitBusy = false; UpdateChatControls(); }
            }
        }
    }

    private async void OnGitPull(object sender, RoutedEventArgs args) => await RunGitOperationAsync("プル", (state, token) => git.PullAsync(state, token), changesFiles: true);
    private async void OnGitPush(object sender, RoutedEventArgs args) => await RunGitOperationAsync("プッシュ", (state, token) => git.PushAsync(state, token));
    private void SetGitOperationMessage(string message)
    {
        GitOperationStatusText.Text = message.Replace("\r", "").Replace('\n', ' ').Trim();
        GitOperationStatusText.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(GitOperationStatusText, message);
    }

    private void AddGitFileMenu(ListView list, bool staged)
    {
        list.ContextRequested += (sender, args) => {
            if (!GitAvailable) return;
            var source = args.OriginalSource as DependencyObject;
            GitChange? change = null;
            while (source != null && source != list) {
                if (source is FrameworkElement { DataContext: GitChange item }) { change = item; break; }
                source = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(source);
            }
            if (change != null) list.SelectedItem = change;
            else if (!args.TryGetPosition(list, out _)) change = list.SelectedItem as GitChange;
            if (change == null) return;
            var selected = change;
            var menu = new MenuFlyout();
            var action = new MenuFlyoutItem { Text = staged ? "このファイルのステージを解除" : "このファイルをステージ", Icon = new SymbolIcon(staged ? Symbol.Remove : Symbol.Add) };
            action.Click += async (_, _) => await RunGitOperationAsync(staged ? "ステージ解除" : "ステージング", (state, token) => staged ? git.UnstageAsync(state, selected, token) : git.StageAsync(state, selected, token));
            menu.Items.Add(action);
            if (args.TryGetPosition(list, out var position)) menu.ShowAt(list, position);
            else menu.ShowAt(list.ContainerFromItem(selected) as FrameworkElement ?? list);
            args.Handled = true;
        };
    }
    private void OnGitBranchClick(object sender, RoutedEventArgs args)
    {
        if (!GitAvailable || gitSnapshot is not { } state) return;
        var menu = new MenuFlyout();
        var switchBranch = new MenuFlyoutItem { Text = "ブランチを切り替え…" };
        switchBranch.Click += async (_, _) => {
            try {
                var branches = state.Branches.Where(branch => !branch.Current).OrderBy(branch => branch.Remote).ThenBy(branch => branch.Name).ToList();
                var choices = new ComboBox { Header = "切り替え先", PlaceholderText = "ブランチを選択", MinWidth = 300, HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemsSource = branches.Select(branch => branch.Name + (branch.Remote ? "（リモート）" : "")).ToList() };
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock { Text = "現在: " + state.Branch, TextWrapping = TextWrapping.Wrap });
                if (branches.Count > 0) content.Children.Add(choices);
                else content.Children.Add(new TextBlock { Text = "切り替え可能なブランチがありません。リモートブランチを取得するには Git 欄からフェッチしてください。", TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
                var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "ブランチを切り替え", Content = content, PrimaryButtonText = "切り替え", CloseButtonText = "キャンセル", IsPrimaryButtonEnabled = false };
                choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedIndex >= 0;
                if (await dialog.ShowAsync() == ContentDialogResult.Primary && choices.SelectedIndex >= 0) {
                    var branch = branches[choices.SelectedIndex];
                    await RunGitOperationAsync("ブランチ切り替え", (repository, token) => git.SwitchAsync(repository, branch, token), changesFiles: true, expected: state);
                }
            }
            catch (Exception ex) { SetGitOperationMessage(ex.Message); }
        };
        menu.Items.Add(switchBranch);
        var create = new MenuFlyoutItem { Text = "新規ブランチを作成…", Icon = new SymbolIcon(Symbol.Add) };
        create.Click += async (_, _) => {
            try {
                var name = new TextBox { Header = "ブランチ名", PlaceholderText = "feature/example", MinWidth = 300 };
                var dialog = new ContentDialog { XamlRoot = RootLayout.XamlRoot, Title = "ブランチを作成して切り替え", Content = name, PrimaryButtonText = "作成", CloseButtonText = "キャンセル", DefaultButton = ContentDialogButton.Primary };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    await RunGitOperationAsync("ブランチ作成", (repository, token) => git.CreateBranchAsync(repository, name.Text.Trim(), token), changesFiles: true, expected: state);
            }
            catch (Exception ex) { SetGitOperationMessage(ex.Message); }
        };
        menu.Items.Add(create);
        if (state.Branches.Count > 0) menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var branch in state.Branches.OrderBy(branch => branch.Remote).ThenBy(branch => branch.Name)) {
            var item = new MenuFlyoutItem { Text = (branch.Current ? "✓ " : "") + branch.Name + (branch.Remote ? "（リモート）" : ""), IsEnabled = !branch.Current };
            item.Click += async (_, _) => await RunGitOperationAsync("ブランチ切り替え", (repository, token) => git.SwitchAsync(repository, branch, token), changesFiles: true, expected: state);
            menu.Items.Add(item);
        }
        menu.ShowAt(GitBranchButton);
    }
}
