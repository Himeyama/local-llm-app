using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private double consoleDragHeight, consoleDragDistance;
    private double explorerDragHeight, explorerDragDistance;
    private double gitDragHeight, gitDragDistance;
    private double explorerHeight = 240, gitHeight = 320;
    private bool chatSectionExpanded = true, gitSectionExpanded = true, arrangingHistory;
    private const double SectionHeaderHeight = 32;
    private bool restoringHistory;

    private void RestoreHistoryLayout()
    {
        restoringHistory = true;
        try {
            explorerHeight = double.IsFinite(settings.ExplorerPaneHeight) ? Math.Max(100, settings.ExplorerPaneHeight) : 240;
            gitHeight = double.IsFinite(settings.GitPaneHeight) ? Math.Max(160, settings.GitPaneHeight) : 320;
            ExplorerPane.SetExpanded(settings.ExplorerSectionExpanded);
            if (!settings.ChatSectionExpanded) OnToggleChatSection(this, new RoutedEventArgs());
            if (!settings.GitSectionExpanded) OnToggleGitSection(this, new RoutedEventArgs());
        }
        finally { restoringHistory = false; }
        UpdateHistoryLayout();
    }

    private void SaveHistoryLayout()
    {
        if (restoringHistory) return;
        settings.ExplorerPaneHeight = explorerHeight;
        settings.GitPaneHeight = gitHeight;
        settings.ExplorerSectionExpanded = ExplorerPane.IsExpanded;
        settings.ChatSectionExpanded = chatSectionExpanded;
        settings.GitSectionExpanded = gitSectionExpanded;
        try { settings.Save(); }
        catch (Exception ex) { ShowError("パネルの配置を保存できません: " + ex.Message); }
    }

    private void OnExplorerExpansionChanged()
    {
        UpdateHistoryLayout();
        SaveHistoryLayout();
    }

    private void OnConsoleResizeStarted(object sender, DragStartedEventArgs args)
    {
        consoleDragHeight = ConsoleHost.ActualHeight; consoleDragDistance = 0;
    }

    private void OnConsoleResizeDelta(object sender, DragDeltaEventArgs args)
    {
        consoleDragDistance += args.VerticalChange;
        SetConsoleHeight(consoleDragHeight - consoleDragDistance, savePreference: true);
    }

    private void OnConsoleResizeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        args.Handled = true;
        SetConsoleHeight(ConsoleHost.Height + (args.Key == VirtualKey.Up ? 16 : -16), savePreference: true);
    }

    private void SaveWorkspacePaneLayout()
    {
        settings.EditorWidthRatio = editorRatio;
        try { settings.Save(); }
        catch (Exception ex) { ShowError("パネルの配置を保存できません: " + ex.Message); }
    }

    private void SetConsoleHeight(double height, bool savePreference = false)
    {
        if (ChatWorkspaceLayout.ActualHeight <= 0) return;
        var topMinimum = EditorPane.Visibility == Visibility.Visible ? 120 :
            Math.Max(180, VisibleHeight(ChatComposer) + VisibleHeight(ChatActivityPanel) + VisibleHeight(PendingChatScroll) + 64);
        var maximum = Math.Max(0, ChatWorkspaceLayout.ActualHeight - topMinimum - 8);
        ConsoleHost.Height = Math.Clamp(height, Math.Min(120, maximum), maximum);
        if (savePreference) { settings.ConsoleHeight = ConsoleHost.Height; SaveWorkspacePaneLayout(); }
    }

    private void OnConsoleLayoutChanged(object sender, SizeChangedEventArgs args)
        => ApplyConsoleHeight();

    private void ApplyConsoleHeight()
    {
        if (ConsoleHost != null && ConsolePanel?.Visibility == Visibility.Visible) SetConsoleHeight(double.IsFinite(settings.ConsoleHeight) ? settings.ConsoleHeight : 280);
    }

    private void OnExplorerResizeStarted(object sender, DragStartedEventArgs args)
    {
        explorerDragHeight = ExplorerPane.ActualHeight; explorerDragDistance = 0;
    }

    private void OnExplorerResizeDelta(object sender, DragDeltaEventArgs args)
    {
        explorerDragDistance += args.VerticalChange;
        SetExplorerHeight(explorerDragHeight + explorerDragDistance);
    }

    private void OnExplorerResizeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        args.Handled = true;
        SetExplorerHeight(ExplorerPane.ActualHeight + (args.Key == VirtualKey.Down ? 16 : -16));
    }

    private void SetExplorerHeight(double height)
    {
        if (!chatSectionExpanded && gitSectionExpanded && GitPanel.Visibility == Visibility.Visible) {
            var total = ExplorerPane.ActualHeight + GitPanel.ActualHeight;
            explorerHeight = Math.Clamp(height, Math.Min(100, total / 2), Math.Max(100, total - 160));
            gitHeight = Math.Max(160, total - explorerHeight);
        }
        else explorerHeight = Math.Max(SectionHeaderHeight, height);
        UpdateHistoryLayout();
        SaveHistoryLayout();
    }

    private void OnGitResizeStarted(object sender, DragStartedEventArgs args)
    {
        gitDragHeight = GitPanel.ActualHeight; gitDragDistance = 0;
    }

    private void OnGitResizeDelta(object sender, DragDeltaEventArgs args)
    {
        gitDragDistance += args.VerticalChange;
        SetGitHeight(gitDragHeight - gitDragDistance);
    }

    private void OnGitResizeKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
        args.Handled = true;
        SetGitHeight(GitPanel.Height + (args.Key == VirtualKey.Up ? 16 : -16));
    }

    private void SetGitHeight(double height)
    {
        gitHeight = Math.Max(SectionHeaderHeight, height);
        UpdateHistoryLayout();
        SaveHistoryLayout();
    }

    private void OnToggleChatSection(object sender, RoutedEventArgs args)
    {
        chatSectionExpanded = !chatSectionExpanded;
        NewChatButton.Visibility = SessionList.Visibility = chatSectionExpanded ? Visibility.Visible : Visibility.Collapsed;
        ChatHistoryPanel.RowSpacing = chatSectionExpanded ? 12 : 0;
        ChatSectionChevron.Glyph = chatSectionExpanded ? "\uE70D" : "\uE76C";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ChatSectionToggle, chatSectionExpanded ? "チャットを折りたたむ" : "チャットを展開");
        UpdateHistoryLayout();
        SaveHistoryLayout();
    }

    private void OnToggleGitSection(object sender, RoutedEventArgs args)
    {
        gitSectionExpanded = !gitSectionExpanded;
        gitSectionBody.Visibility = gitSectionExpanded ? Visibility.Visible : Visibility.Collapsed;
        gitSectionChevron.Glyph = gitSectionExpanded ? "\uE70D" : "\uE76C";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(gitSectionToggle, gitSectionExpanded ? "Git を折りたたむ" : "Git を展開");
        UpdateHistoryLayout();
        SaveHistoryLayout();
    }

    private void OnHistoryLayoutChanged(object sender, SizeChangedEventArgs args) => UpdateHistoryLayout();

    private void UpdateHistoryLayout()
    {
        if (arrangingHistory || HistoryPane == null || GitPanel == null || ChatHistoryRow == null || HistoryPane.ActualHeight <= 0) return;
        arrangingHistory = true;
        try {
            var explorerVisible = ExplorerPane.Visibility == Visibility.Visible;
            var gitVisible = GitPanel.Visibility == Visibility.Visible;
            var explorerOpen = explorerVisible && ExplorerPane.IsExpanded;
            var gitOpen = gitVisible && gitSectionExpanded;
            ExplorerResizeHandle.Visibility = explorerOpen && (chatSectionExpanded || gitOpen) ? Visibility.Visible : Visibility.Collapsed;
            GitResizeHandle.Visibility = gitOpen && chatSectionExpanded ? Visibility.Visible : Visibility.Collapsed;
            ChatHistoryPanel.MinHeight = chatSectionExpanded ? 144 : SectionHeaderHeight;
            ChatHistoryRow.Height = chatSectionExpanded ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            var available = Math.Max(0, HistoryPane.ActualHeight - HistoryPane.Padding.Top - HistoryPane.Padding.Bottom -
                (ExplorerResizeHandle.Visibility == Visibility.Visible ? 8 : 0) - (GitResizeHandle.Visibility == Visibility.Visible ? 8 : 0) - ChatHistoryPanel.MinHeight);
            var minimumExplorer = explorerVisible ? explorerOpen ? 100 : SectionHeaderHeight : 0;
            var minimumGit = gitVisible ? gitOpen ? 160 : SectionHeaderHeight : 0;
            // Reserve each visible title even when the window becomes very small.
            var maxExplorer = Math.Max(explorerVisible ? SectionHeaderHeight : 0, available - minimumGit);
            var actualExplorer = explorerVisible ? Math.Clamp(explorerOpen ? explorerHeight : SectionHeaderHeight, Math.Min(minimumExplorer, maxExplorer), maxExplorer) : 0;
            var maxGit = Math.Max(gitVisible ? SectionHeaderHeight : 0, available - actualExplorer);
            var actualGit = gitVisible ? Math.Clamp(gitOpen ? gitHeight : SectionHeaderHeight, Math.Min(minimumGit, maxGit), maxGit) : 0;
            if (!chatSectionExpanded && (explorerOpen || gitOpen)) {
                var space = Math.Max(0, available - (explorerVisible && !explorerOpen ? SectionHeaderHeight : 0) -
                    (gitVisible && !gitOpen ? SectionHeaderHeight : 0));
                if (explorerOpen && gitOpen) {
                    actualExplorer = Math.Clamp(space * (explorerHeight / (explorerHeight + gitHeight)), Math.Min(100, space / 2), Math.Max(Math.Min(100, space / 2), space - 160));
                    actualGit = space - actualExplorer;
                }
                else if (explorerOpen) actualExplorer = space;
                else actualGit = space;
            }
            if (explorerVisible) ExplorerPane.Height = actualExplorer;
            if (gitVisible) GitPanel.Height = actualGit;
        }
        finally { arrangingHistory = false; }
    }

    private static double VisibleHeight(FrameworkElement element) => element.Visibility == Visibility.Visible
        ? element.ActualHeight + element.Margin.Top + element.Margin.Bottom : 0;

    private void RefreshWorkspaceExplorer()
    {
        RefreshGitContext();
        var workspace = currentChat?.Workspace ?? "";
        var visibility = string.IsNullOrWhiteSpace(workspace) ? Visibility.Collapsed : Visibility.Visible;
        ExplorerPane.Visibility = visibility;
        ExplorerPane.SetWorkspace(workspace);
        UpdateHistoryLayout();
    }
}
