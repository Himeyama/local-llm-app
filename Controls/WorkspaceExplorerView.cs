using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace LocalLlm.Gui.Controls;

public sealed partial class WorkspaceExplorerView : Grid
{
    public event Action<string>? FileOpenRequested;
    public event Action<string, string>? PathRenamed;
    public event Action? ExpansionChanged;
    public bool IsExpanded { get; private set; } = true;
    private TreeView tree => ExplorerTree;
    private TextBlock status => ExplorerStatus;
    private readonly HashSet<TreeViewNode> loading = new();
    private string workspace = "";
    private int version;

    public WorkspaceExplorerView()
    {
        InitializeComponent();
        RefreshButton.Click += async (_, _) => await RunCommandAsync(() => ReloadAsync());
        NewFileButton.Click += async (_, _) => await RunCommandAsync(() => CreateItemAsync(false, CreationDirectory()));
        NewFolderButton.Click += async (_, _) => await RunCommandAsync(() => CreateItemAsync(true, CreationDirectory()));
        tree.Expanding += async (_, args) => await LoadNodeAsync(args.Node, version);
        tree.ItemInvoked += (_, args) => {
            if (args.InvokedItem is TreeViewNode { Content: WorkspaceEntry { CanExpand: true } } node)
                node.IsExpanded = !node.IsExpanded;
            else if (args.InvokedItem is TreeViewNode { Content: WorkspaceEntry { IsDirectory: false } entry }) OpenFile(entry);
        };
        tree.ContextRequested += OnContextRequested;
        tree.PreviewKeyDown += OnKeyDown;
    }

    public void SetWorkspace(string path)
    {
        path = string.IsNullOrWhiteSpace(path) ? "" : path;
        if (string.Equals(workspace, path, StringComparison.OrdinalIgnoreCase)) return;
        workspace = path;
        ToolTipService.SetToolTip(this, workspace);
        _ = ReloadAsync();
    }

    private void OnToggleSection(object sender, RoutedEventArgs args)
        => SetExpanded(!IsExpanded);

    public void SetExpanded(bool expanded)
    {
        IsExpanded = expanded;
        ExplorerTree.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExplorerStatus.Visibility = IsExpanded && ExplorerStatus.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExplorerSectionChevron.Glyph = IsExpanded ? "\uE70D" : "\uE76C";
        RowSpacing = IsExpanded ? 6 : 0;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(ExplorerSectionToggle, IsExpanded ? "エクスプローラーを折りたたむ" : "エクスプローラーを展開");
        ExpansionChanged?.Invoke();
    }

    private async Task ReloadAsync(string? revealPath = null)
    {
        var expanded = WalkNodes().Where(node => node.IsExpanded).Select(node => ((WorkspaceEntry)node.Content).Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedPath = revealPath ?? (tree.SelectedNode?.Content as WorkspaceEntry)?.Path;
        var revision = ++version;
        tree.RootNodes.Clear(); loading.Clear(); status.Visibility = Visibility.Collapsed;
        if (workspace.Length == 0) return;
        try {
            var path = Path.GetFullPath(workspace);
            if (selectedPath != null) {
                for (var parent = Path.GetDirectoryName(selectedPath); parent != null && (parent.Equals(path, StringComparison.OrdinalIgnoreCase) || parent.StartsWith(Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)); parent = Path.GetDirectoryName(parent)) expanded.Add(parent);
                if (Directory.Exists(selectedPath)) expanded.Add(selectedPath);
            }
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
            var root = CreateNode(new WorkspaceEntry(name.Length == 0 ? path : name, path, true, false));
            tree.RootNodes.Add(root);
            await LoadNodeAsync(root, revision);
            if (revision != version) return;
            root.IsExpanded = true;
            await RestoreExpandedAsync(root, expanded, revision);
            if (revision == version && selectedPath != null)
                tree.SelectedNode = WalkNodes().FirstOrDefault(node => ((WorkspaceEntry)node.Content).Path.Equals(selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) { if (revision == version) ShowStatus(ex.Message); }
    }

    private IEnumerable<TreeViewNode> WalkNodes()
    {
        foreach (var root in tree.RootNodes) foreach (var node in Walk(root)) yield return node;
        static IEnumerable<TreeViewNode> Walk(TreeViewNode node)
        {
            yield return node;
            foreach (var child in node.Children) foreach (var nested in Walk(child)) yield return nested;
        }
    }

    private async Task RestoreExpandedAsync(TreeViewNode node, HashSet<string> expanded, int revision)
    {
        foreach (var child in node.Children.ToArray()) {
            if (revision != version) return;
            if (child.Content is WorkspaceEntry { CanExpand: true } entry && expanded.Contains(entry.Path)) {
                await LoadNodeAsync(child, revision);
                if (revision != version) return;
                child.IsExpanded = true;
                await RestoreExpandedAsync(child, expanded, revision);
            }
        }
    }

    private static TreeViewNode CreateNode(WorkspaceEntry entry)
    {
        // RootNodes templates receive a TreeViewNode, whose Content must be data.
        return new TreeViewNode { Content = entry, HasUnrealizedChildren = entry.CanExpand };
    }

    private async Task LoadNodeAsync(TreeViewNode node, int revision)
    {
        if (!node.HasUnrealizedChildren || node.Content is not WorkspaceEntry entry || !loading.Add(node)) return;
        try {
            var entries = await Task.Run(() => WorkspaceFiles.ReadDirectory(entry.Path));
            if (revision != version) return;
            node.Children.Clear();
            foreach (var child in entries) node.Children.Add(CreateNode(child));
            node.HasUnrealizedChildren = false;
        }
        catch (Exception ex) { if (revision == version) ShowStatus(ex.Message); }
        finally { loading.Remove(node); }
    }

    private async void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (tree.SelectedNode is not { Content: WorkspaceEntry entry } node) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        Func<Task>? action = args.Key switch {
            VirtualKey.C when ctrl => () => CopyItemAsync(entry, false),
            VirtualKey.X when ctrl => () => CopyItemAsync(entry, true),
            VirtualKey.V when ctrl => () => PasteAsync(entry),
            VirtualKey.F2 => () => RenameItemAsync(entry),
            VirtualKey.Delete => () => DeleteItemAsync(entry),
            _ => null
        };
        if (action != null) { args.Handled = true; await RunCommandAsync(action); return; }
        if (args.Key == VirtualKey.Enter) {
            args.Handled = true;
            if (entry.CanExpand) node.IsExpanded = !node.IsExpanded;
            else if (!entry.IsDirectory) OpenFile(entry);
        }
    }

    private void OpenFile(WorkspaceEntry entry)
    {
        try { FileOpenRequested?.Invoke(entry.Path); }
        catch (Exception ex) { ShowStatus(ex.Message); }
    }

    private void ShowStatus(string message) { status.Text = message; status.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed; }
}
