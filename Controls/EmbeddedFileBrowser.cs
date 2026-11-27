using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace LocalLlm.Gui.Controls;

public sealed class EmbeddedFileBrowser : Grid
{
    private const string Computer = "::computer";
    private readonly bool folders, multiple;
    private readonly HashSet<string> extensions;
    private readonly TextBox address = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox search = new() { PlaceholderText = "検索" };
    private readonly TextBox name = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListView items = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock notice = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button back = IconButton("\uE72B", "戻る"), forward = IconButton("\uE72A", "進む"), up = IconButton("\uE74A", "上の階層");
    private readonly StackPanel shortcuts = new() { Spacing = 3, Width = 160 };
    private readonly List<string> history = new();
    private List<Entry> found = new();
    private string directory = "";
    private int historyIndex = -1, loadVersion;
    private sealed record Entry(string Path, string Name, bool IsDirectory);
    public event Action<string[]>? Selected;
    public event Action? Cancelled;

    public EmbeddedFileBrowser(string initialPath, bool folders, bool multiple, params string[] extensions)
    {
        this.folders = folders; this.multiple = multiple;
        this.extensions = new(extensions, StringComparer.OrdinalIgnoreCase);
        Padding = new Thickness(24); RowSpacing = 8;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Children.Add(new TextBlock { Text = folders ? "フォルダーを選択" : "ファイルを選択", FontSize = 24 });
        var toolbar = new Grid { ColumnSpacing = 5 };
        for (int i = 0; i < 3; i++) toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        toolbar.Children.Add(back); Grid.SetColumn(forward, 1); toolbar.Children.Add(forward); Grid.SetColumn(up, 2); toolbar.Children.Add(up);
        Grid.SetColumn(address, 3); toolbar.Children.Add(address); Grid.SetColumn(search, 4); toolbar.Children.Add(search);
        Grid.SetRow(toolbar, 1); Children.Add(toolbar);
        var refresh = IconButton("\uE72C", "再読み込み"); refresh.Click += (_, _) => _ = NavigateAsync(directory, false);
        var newFolder = IconButton("\uE8F4", "新しいフォルダー");
        var folderName = new TextBox { PlaceholderText = "フォルダー名", Width = 230 };
        var create = new Button { Content = "作成" };
        var createPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Visibility = Visibility.Collapsed };
        createPanel.Children.Add(folderName); createPanel.Children.Add(create);
        newFolder.Click += (_, _) => { createPanel.Visibility = Visibility.Visible; folderName.Focus(FocusState.Programmatic); };
        create.Click += (_, _) => { try { var value = folderName.Text.Trim(); if (directory == Computer || value.Length == 0 || Path.GetFileName(value) != value || value is "." or "..") throw new ArgumentException("フォルダー名を入力してください。"); var target = Path.Combine(directory, value); if (Directory.Exists(target) || File.Exists(target)) throw new IOException("同じ名前の項目があります。"); Directory.CreateDirectory(target); createPanel.Visibility = Visibility.Collapsed; folderName.Text = ""; _ = NavigateAsync(directory, false); } catch (Exception ex) { notice.Text = "作成できません: " + ex.Message; } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; actions.Children.Add(newFolder); actions.Children.Add(refresh); actions.Children.Add(createPanel);
        Grid.SetRow(actions, 2); Children.Add(actions);
        var browser = new Grid { ColumnSpacing = 8 }; browser.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); browser.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        browser.Children.Add(new ScrollViewer { Content = shortcuts, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        items.SelectionMode = multiple ? ListViewSelectionMode.Extended : ListViewSelectionMode.Single;
        Grid.SetColumn(items, 1); browser.Children.Add(items); Grid.SetRow(browser, 3); Children.Add(browser);
        Grid.SetRow(notice, 4); Children.Add(notice);
        if (!folders) {
            var fields = new Grid { ColumnSpacing = 8, RowSpacing = 6 };
            fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) }); fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            fields.Children.Add(new TextBlock { Text = "ファイル名", VerticalAlignment = VerticalAlignment.Center }); Grid.SetColumn(name, 1); fields.Children.Add(name);
            var label = new TextBlock { Text = "ファイルの種類", VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(label, 1); fields.Children.Add(label);
            var type = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch }; type.Items.Add(string.Join(", ", extensions.Select(x => "*" + x))); type.SelectedIndex = 0; Grid.SetRow(type, 1); Grid.SetColumn(type, 1); fields.Children.Add(type);
            Grid.SetRow(fields, 5); Children.Add(fields);
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var select = new Button { Content = folders ? "選択" : "開く" }; select.Click += (_, _) => Confirm();
        var cancel = new Button { Content = "キャンセル" }; cancel.Click += (_, _) => Cancelled?.Invoke();
        buttons.Children.Add(select); buttons.Children.Add(cancel); Grid.SetRow(buttons, 6); Children.Add(buttons);
        AutomationProperties.SetName(address, "フォルダーのパス"); AutomationProperties.SetName(search, "現在のフォルダーを検索"); AutomationProperties.SetName(name, "ファイル名"); AutomationProperties.SetName(items, "フォルダーとファイル");
        AddShortcut("ホーム", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "\uE80F");
        AddShortcut("デスクトップ", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "\uE7F4"); AddShortcut("PC", Computer, "\uE770");
        AddShortcut("ドキュメント", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "\uE8A5"); AddShortcut("ピクチャ", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "\uEB9F");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); if (profile.Length > 0) AddShortcut("ダウンロード", Path.Combine(profile, "Downloads"), "\uE896");
        foreach (var drive in DriveInfo.GetDrives()) AddShortcut(drive.Name, drive.Name, "\uEDA2");
        back.Click += (_, _) => { if (historyIndex > 0) _ = NavigateAsync(history[--historyIndex], false); };
        forward.Click += (_, _) => { if (historyIndex + 1 < history.Count) _ = NavigateAsync(history[++historyIndex], false); };
        up.Click += (_, _) => { if (directory != Computer) _ = NavigateAsync(Directory.GetParent(directory)?.FullName ?? Computer, true); };
        address.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; _ = NavigateAsync(address.Text == "PC" ? Computer : address.Text, true); } };
        search.TextChanged += (_, _) => ShowEntries();
        items.SelectionChanged += (_, _) => { if (!folders && items.SelectedItem is ListViewItem { Tag: Entry entry } && !entry.IsDirectory) name.Text = entry.Name; };
        items.DoubleTapped += (_, _) => Activate();
        items.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Activate(); } };
        name.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { e.Handled = true; Confirm(); } };
        KeyDown += (_, e) => { if (e.Key == VirtualKey.Escape) { e.Handled = true; Cancelled?.Invoke(); } };
        string start = Environment.ExpandEnvironmentVariables(initialPath.Trim().Trim('"'));
        if (File.Exists(start)) start = Path.GetDirectoryName(start) ?? "";
        if (!Directory.Exists(start)) start = profile;
        _ = NavigateAsync(start, true);
    }
    private static Button IconButton(string glyph, string label)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 16 }, MinWidth = 36, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label); return button;
    }
    private void AddShortcut(string label, string path, string glyph)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; content.Children.Add(ShellFileIcons.Create(path, true, location: true)); content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        button.Click += (_, _) => _ = NavigateAsync(path, true); shortcuts.Children.Add(button);
    }
    private async Task NavigateAsync(string path, bool addHistory)
    {
        int version = ++loadVersion;
        try {
            string full = path == Computer ? Computer : Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            var result = await Task.Run(() => full == Computer ? DriveInfo.GetDrives().Select(x => new Entry(x.Name, x.Name, true)).ToArray() :
                new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System })
                .Select(x => new Entry(x.FullName, x.Name, (x.Attributes & FileAttributes.Directory) != 0)).OrderByDescending(x => x.IsDirectory).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
            if (version != loadVersion) return;
            directory = full; address.Text = full == Computer ? "PC" : full; found = result.ToList(); notice.Text = ""; name.Text = "";
            if (addHistory && (historyIndex < 0 || !string.Equals(history[historyIndex], full, StringComparison.OrdinalIgnoreCase))) { if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1); history.Add(full); historyIndex = history.Count - 1; }
            back.IsEnabled = historyIndex > 0; forward.IsEnabled = historyIndex + 1 < history.Count; up.IsEnabled = full != Computer;
            ShowEntries();
        } catch (Exception ex) { if (version == loadVersion) { notice.Text = "フォルダーを開けません: " + ex.Message; address.Text = directory == Computer ? "PC" : directory; } }
    }
    private void ShowEntries()
    {
        items.Items.Clear();
        foreach (var entry in found) {
            if (!entry.Name.Contains(search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) || (!entry.IsDirectory && (folders || !extensions.Contains(Path.GetExtension(entry.Name))))) continue;
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; content.Children.Add(ShellFileIcons.Create(entry.Path, entry.IsDirectory, location: directory == Computer)); content.Children.Add(new TextBlock { Text = entry.Name, VerticalAlignment = VerticalAlignment.Center });
            var item = new ListViewItem { Content = content, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch }; AutomationProperties.SetName(item, entry.Name + (entry.IsDirectory ? "、フォルダー" : "、ファイル")); items.Items.Add(item);
        }
    }
    private void Activate()
    {
        if (items.SelectedItem is not ListViewItem { Tag: Entry entry }) return;
        if (entry.IsDirectory) _ = NavigateAsync(entry.Path, true); else Confirm();
    }
    private void Confirm()
    {
        var selected = items.SelectedItems.OfType<ListViewItem>().Select(x => x.Tag).OfType<Entry>().ToArray();
        if (folders) {
            var target = selected.Length == 1 && selected[0].IsDirectory ? selected[0].Path : directory;
            if (Directory.Exists(target)) Selected?.Invoke(new[] { target }); else notice.Text = "フォルダーを選択してください。";
        } else {
            var paths = selected.Where(x => !x.IsDirectory && File.Exists(x.Path)).Select(x => x.Path).ToArray();
            if (paths.Length == 0 && directory != Computer && name.Text.Trim().Length > 0) { try { var path = Path.GetFullPath(Path.Combine(directory, name.Text.Trim())); if (File.Exists(path) && extensions.Contains(Path.GetExtension(path))) paths = new[] { path }; } catch (Exception ex) { notice.Text = ex.Message; return; } }
            if (paths.Length > 0) Selected?.Invoke(paths); else notice.Text = "ファイルを選択してください。";
        }
    }
}
