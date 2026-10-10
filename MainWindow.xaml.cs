using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using Microsoft.Web.WebView2.Core;

namespace LocalLlm.Gui;

public sealed partial class MainWindow : Window
{
    private readonly ProcessRunner runner = new();
    private readonly Queue<string> logs = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer spinnerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly ServerStartupStatus serverStatus = new();
    private readonly WebStartupStatus webStatus = new();
    private Settings settings = new();
    private bool ready, checking, closing;
    private string page = "chat";
    private bool historyExpanded = true;
    private int loadedPort;
    private Task? browserInitialization;
    private Action? saveServerSettings;
    private TextBox[] serverLaunchInputs = Array.Empty<TextBox>();
    private Button[] cliButtons = Array.Empty<Button>();
    private bool ServerActive => serverStatus.IsConnected || serverStatus.IsStarting || runner.IsRunning("llama-server");
    private string WebUrl => $"http://127.0.0.1:{settings.WebPort}";

    public MainWindow()
    {
        InitializeComponent();
        Title = "Local LLM — ローカル AI ワークスペース";
        InitializeWindowChrome();
        ResizeInitialWindow();
        try { settings = Settings.Load(); } catch (Exception e) { ShowError("設定ファイルを読み込めません: " + e.Message); }
        runner.Log += message => DispatcherQueue.TryEnqueue(() => {
            AddLog(message);
            if (message.StartsWith("[llama-server] ", StringComparison.Ordinal)) { serverStatus.ObserveLog(message); UpdateStatus(); }
            if (message.StartsWith("[Open WebUI] ", StringComparison.Ordinal)) { webStatus.ObserveLog(message); UpdateStatus(); }
        });
        runner.Exited += (name, code, stopRequested) => DispatcherQueue.TryEnqueue(() => {
            if (closing) return;
            if (name == "llama-server") serverStatus.Finish(code, stopRequested);
            else if (name == "Open WebUI") { webStatus.Finish(code, stopRequested); loadedPort = 0; }
            else return;
            UpdateStatus();
            if (code != 0 && !stopRequested) ShowError($"{name} が終了しました（コード {code}）。ログを確認してください。");
        });
        timer.Tick += async (_, _) => await RefreshStatusAsync();
        spinnerTimer.Tick += (_, _) => { serverStatus.Tick(); webStatus.Tick(); UpdateStatus(); };
        Closed += (_, _) => { closing = true; timer.Stop(); spinnerTimer.Stop(); runner.Dispose(); http.Dispose(); Browser.Close(); };
        ready = true;
        InitializeChat();
        BuildPage();
        timer.Start();
        _ = RefreshStatusAsync();
    }

    private void InitializeWindowChrome()
    {
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            ExtendsContentIntoTitleBar = true;
            // TitleBar handles caption insets, drag/passthrough regions and DPI
            // changes, retaining native maximize, snap, system menu and close.
            SetTitleBar(AppTitleBar);
            UpdateCaptionColors();
            RootLayout.ActualThemeChanged += (_, _) => UpdateCaptionColors();
        }
        else
        {
            AppTitleBar.Visibility = Visibility.Collapsed;
        }
    }
    private void UpdateCaptionColors()
    {
        var dark = RootLayout.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        titleBar.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.DarkGray : Microsoft.UI.Colors.Gray;
        titleBar.ButtonHoverBackgroundColor = dark ? Microsoft.UI.Colors.DimGray : Microsoft.UI.Colors.LightGray;
        titleBar.ButtonHoverForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        titleBar.ButtonPressedBackgroundColor = dark ? Microsoft.UI.Colors.Gray : Microsoft.UI.Colors.Silver;
        titleBar.ButtonPressedForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
    }
    private void ResizeInitialWindow()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi > 0 ? dpi / 96.0 : 1.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = WindowSizing.InitialClientSize(scale, work.Width, work.Height);
        AppWindow.ResizeClient(new Windows.Graphics.SizeInt32(size.Width, size.Height));
        var outer = AppWindow.Size;
        AppWindow.Move(new Windows.Graphics.PointInt32(work.X + Math.Max(0, (work.Width - outer.Width) / 2),
            work.Y + Math.Max(0, (work.Height - outer.Height) / 2)));
    }
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void OnPaneToggleRequested(TitleBar sender, object args)
    {
        if (page != "chat") return;
        historyExpanded = !historyExpanded;
        HistoryPane.Visibility = historyExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddLog(string message)
    {
        if (closing) return;
        logs.Enqueue($"{DateTime.Now:HH:mm:ss} {message}");
        while (logs.Count > 600) logs.Dequeue();
        if (page == "logs") LogView.Text = string.Join(Environment.NewLine, logs);
    }
    private void ShowError(string message)
    {
        Notice.Severity = InfoBarSeverity.Error; Notice.Title = "操作を完了できません"; Notice.Message = message; Notice.IsOpen = true;
        AddLog(message);
    }
    private void Execute(Action action)
    {
        try { Notice.IsOpen = false; action(); } catch (Exception e) { ShowError(e.Message); }
    }
    private string Script(string name)
    {
        var path = Path.Combine(settings.RepositoryRoot, name);
        if (!File.Exists(path)) throw new FileNotFoundException("起動スクリプトが見つかりません。GUI と起動スクリプトの配置を確認してください。", path);
        return path;
    }
    private void StartScript(string name, string script, Dictionary<string, string?>? args = null)
    {
        runner.Start(name, ProcessRunner.PowerShell, settings.RepositoryRoot,
            ProcessRunner.ScriptArguments(Script(script), args ?? new()));
    }
    private void OnMenuClick(object sender, RoutedEventArgs args)
    {
        if (!ready || sender is not Microsoft.UI.Xaml.Controls.Primitives.ToggleButton item) return;
        page = item.Tag.ToString()!;
        BuildPage();
    }
    private TextBox Input(string label, string value = "", bool multiline = false)
    {
        var box = new TextBox { Header = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Text = value, HorizontalAlignment = HorizontalAlignment.Stretch, TextWrapping = TextWrapping.Wrap, AcceptsReturn = multiline };
        Form.Children.Add(box); return box;
    }
    private NumberBox Number(string label, int value, int min, int max, int step = 1)
    {
        var box = new NumberBox { Header = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, Value = value, Minimum = min, Maximum = max, SmallChange = step, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = Math.Min(280, Form.Width), HorizontalAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, label);
        Form.Children.Add(box); return box;
    }
    private StackPanel? settingGroup;
    private void BeginSettingGroup()
    {
        var group = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='8' HorizontalAlignment='Stretch' />");
        settingGroup = new StackPanel { Spacing = 0 };
        group.Child = settingGroup;
        Form.Children.Add(group);
    }
    private T SettingCard<T>(string title, string description, T control) where T : FrameworkElement
    {
        Form.Children.Remove(control);
        if (control is TextBox text) text.Header = null;
        if (control is NumberBox number) number.Header = null;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, title);
        var card = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            "<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='0' Padding='20,14' MinHeight='64' HorizontalAlignment='Stretch' />");
        var layout = new Grid { ColumnSpacing = 24 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var label = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        label.Children.Add(new TextBlock { Text = title, FontSize = 16, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(description))
            label.Children.Add(new TextBlock { Text = description, FontSize = 12, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
        control.VerticalAlignment = VerticalAlignment.Center;
        FrameworkElement editor = control;
        if (control is ToggleSwitch toggle)
        {
            toggle.MinWidth = 0;
            toggle.OnContent = null;
            toggle.OffContent = null;
            toggle.Width = 40;
            var state = new TextBlock { Text = toggle.IsOn ? "オン" : "オフ", VerticalAlignment = VerticalAlignment.Center };
            toggle.Toggled += (_, _) => state.Text = toggle.IsOn ? "オン" : "オフ";
            var togglePanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
            togglePanel.Children.Add(state);
            togglePanel.Children.Add(toggle);
            editor = togglePanel;
        }
        layout.Children.Add(label);
        layout.Children.Add(editor);
        card.Child = layout;
        void Arrange(double width)
        {
            if (!double.IsFinite(width)) width = 920;
            var compact = width < 680;
            Grid.SetColumn(editor, compact ? 0 : 1);
            Grid.SetRow(editor, compact ? 1 : 0);
            layout.RowSpacing = compact ? 12 : 0;
            editor.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            var available = Math.Max(0, width - 40);
            editor.Width = control is ToggleSwitch or Microsoft.UI.Xaml.Controls.Button ? double.NaN : Math.Min(control is NumberBox ? 180 : compact ? available : Math.Min(600, available * 0.55), available);
            editor.MaxWidth = available;
        }
        card.SizeChanged += (_, args) => Arrange(args.NewSize.Width);
        Arrange(Form.Width);
        if (settingGroup == null) BeginSettingGroup();
        if (settingGroup!.Children.LastOrDefault() is Border previous) previous.BorderThickness = new Thickness(0, 0, 0, 1);
        settingGroup.Children.Add(card);
        return control;
    }
    private static int Read(NumberBox box)
    {
        if (double.IsNaN(box.Value) || box.Value != Math.Truncate(box.Value) || box.Value < box.Minimum || box.Value > box.Maximum)
            throw new ArgumentException($"{(box.Header as TextBlock)?.Text ?? Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box)} に範囲内の整数を指定してください。");
        return checked((int)box.Value);
    }
    private void OnFormSizeChanged(object sender, SizeChangedEventArgs args)
    {
        ResizeForm(args.NewSize.Width);
    }
    private void ResizeForm(double width)
    {
        Form.Width = Math.Max(0, page is "server" or "cli" ? width - 12 : Math.Min(920, width - 16));
        foreach (var number in Form.Children.OfType<NumberBox>()) number.Width = Math.Min(280, Form.Width);
    }

    private Button Button(string label, Action action)
    {
        var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => Execute(action); Form.Children.Add(button); return button;
    }
    private void BuildPage()
    {
        HistoryPane.Visibility = page == "chat" && historyExpanded ? Visibility.Visible : Visibility.Collapsed;
        AppTitleBar.IsPaneToggleButtonVisible = page == "chat";
        foreach (var menu in MenuRail.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>()) menu.IsChecked = menu.Tag.ToString() == page;
        PageTitle.Text = page switch { "chat" => currentChat?.Title ?? "チャット", "server" => "llama-server", "web" => "Open WebUI", "cli" => "Claude / Codex", _ => "ログ" };
        PageTitle.Visibility = page == "chat" ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Subtitle = PageTitle.Text;
        ChatPanel.Visibility = page == "chat" ? Visibility.Visible : Visibility.Collapsed;
        saveServerSettings = null;
        ServerCommandBar.Visibility = page == "server" ? Visibility.Visible : Visibility.Collapsed;
        serverLaunchInputs = Array.Empty<TextBox>();
        cliButtons = Array.Empty<Button>();
        Form.Children.Clear();
        settingGroup = null;
        Form.Spacing = 16;
        ResizeForm(FormScroll.ActualWidth);
        FormScroll.Visibility = page is "web" or "logs" or "chat" ? Visibility.Collapsed : Visibility.Visible;
        WebPanel.Visibility = page == "web" ? Visibility.Visible : Visibility.Collapsed;
        LogView.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        UpdateWebControls();
        UpdateChatControls();
        switch (page)
        {
            case "web": _ = EnsureBrowserAsync(); break;
            case "server":
                var lanAccess = SettingCard("LAN に公開", "LAN 内の端末からこの PC の IP アドレス:9931 に接続できます。変更は自動保存され、次回起動時に反映されます。",
                    new ToggleSwitch { OnContent = "オン", OffContent = "オフ", IsOn = settings.ServerLanAccess });
                lanAccess.Toggled += (_, _) => Execute(() => {
                    settings.ServerLanAccess = lanAccess.IsOn;
                    settings.Save();
                });
                BeginSettingGroup();
                var context = SettingCard("コンテキスト長", "65,536 ～ 1,000,000。Claude で使用する場合は 100,000 以上。", Number("コンテキスト長", settings.ContextSize, 65536, 1000000));
                var threads = SettingCard("CPU スレッド", "処理に使用する CPU スレッド数。", Number("CPU スレッド", settings.Threads, 1, 512));
                var mtp = SettingCard("MTP draft tokens", "0 にすると無効になります。", Number("MTP draft tokens", settings.MtpDraftTokens, 0, 16));
                BeginSettingGroup();
                var model = SettingCard("モデル", "GGUF モデルの絶対パス。", Input("モデルの絶対パス", AssetPath(settings.ModelPath, Settings.DefaultModelPath)));
                var mmproj = SettingCard("画像入力用 mmproj", "モデルに対応する mmproj の絶対パス。", Input("対応する mmproj の絶対パス", AssetPath(settings.MmprojPath, Settings.DefaultMmprojPath)));
                var exe = SettingCard("llama-server.exe", "実行ファイルの絶対パス。", Input("llama-server.exe の絶対パス", AssetPath(settings.ServerExe, Settings.DefaultServerExe)));
                serverLaunchInputs = new[] { model, mmproj, exe };
                saveServerSettings = () => {
                    var contextValue = Read(context); var threadsValue = Read(threads); var mtpValue = Read(mtp);
                    var modelPath = AssetPath(model.Text, Settings.DefaultModelPath);
                    var mmprojPath = AssetPath(mmproj.Text, Settings.DefaultMmprojPath);
                    var serverExe = AssetPath(exe.Text, Settings.DefaultServerExe);
                    settings.ContextSize = contextValue; settings.Threads = threadsValue; settings.MtpDraftTokens = mtpValue;
                    settings.ModelPath = modelPath; settings.MmprojPath = mmprojPath; settings.ServerExe = serverExe;
                    settings.Save();
                    model.Text = modelPath; mmproj.Text = mmprojPath; exe.Text = serverExe;
                };
                UpdateServerControls();
                break;
            case "cli":
                var workingDirectory = Input("作業ディレクトリ", string.IsNullOrWhiteSpace(settings.CliWorkingDirectory) ? settings.RepositoryRoot : settings.CliWorkingDirectory);
                Form.Children.Remove(workingDirectory);
                workingDirectory.Header = null;
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(workingDirectory, "作業ディレクトリ");
                var directoryEditor = new Grid { ColumnSpacing = 8 };
                directoryEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                directoryEditor.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var pickDirectory = Button("参照", () => _ = PickCliDirectoryAsync(workingDirectory));
                Form.Children.Remove(pickDirectory);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(pickDirectory, "作業フォルダーを選択");
                ToolTipService.SetToolTip(pickDirectory, "フォルダーを選択");
                pickDirectory.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(pickDirectory, 1);
                directoryEditor.Children.Add(workingDirectory);
                directoryEditor.Children.Add(pickDirectory);
                SettingCard("作業ディレクトリ", "CLI を開くフォルダー。選択時と起動時に保存されます。", directoryEditor);
                var capture = SettingCard("通信キャプチャー", "次回 CLI 起動時に mitmweb で通信をキャプチャーします。", new ToggleSwitch());
                BeginSettingGroup();
                cliButtons = new[] {
                    SettingCard("Claude Code", "llama-server に接続して Windows Terminal で開きます。", Button("Claude Code を開く", () => OpenCli("Start-LocalClaude.ps1", capture.IsOn, workingDirectory.Text))),
                    SettingCard("Codex CLI", "llama-server に接続して Windows Terminal で開きます。", Button("Codex CLI を開く", () => OpenCli("Start-LocalCodex.ps1", capture.IsOn, workingDirectory.Text)))
                };
                foreach (var button in cliButtons) button.IsEnabled = serverStatus.IsConnected;
                break;
            case "logs": LogView.Text = string.Join(Environment.NewLine, logs); break;
        }
    }
    private string AssetPath(string path, string defaultPath) => Path.GetFullPath(
        Environment.ExpandEnvironmentVariables(string.IsNullOrWhiteSpace(path) ? defaultPath : path.Trim()), settings.RepositoryRoot);
    private void OnSaveServerSettings(object sender, RoutedEventArgs e) => Execute(() => saveServerSettings?.Invoke());
    private void OnToggleServer(object sender, RoutedEventArgs e) => Execute(() => {
        if (runner.IsRunning("llama-server")) { runner.Stop("llama-server"); return; }
        if (ServerActive) return;
        saveServerSettings?.Invoke();
        StartServer(new() { ["ModelPath"] = settings.ModelPath, ["MmprojPath"] = settings.MmprojPath, ["ServerExe"] = settings.ServerExe });
    });
    private void StartServer(Dictionary<string, string?> args)
    {
        if (serverStatus.IsStarting || runner.IsRunning("llama-server")) return;
        if (serverStatus.IsConnected) return;
        args["ContextSize"] = settings.ContextSize.ToString(CultureInfo.InvariantCulture);
        args["Threads"] = settings.Threads.ToString(CultureInfo.InvariantCulture);
        args["MtpDraftTokens"] = settings.MtpDraftTokens.ToString(CultureInfo.InvariantCulture);
        if (settings.ServerLanAccess) args["LanAccess"] = null;
        else args.Remove("LanAccess");
        serverStatus.Begin(); UpdateStatus();
        try { StartScript("llama-server", "Start-LlamaServer.ps1", args); }
        catch { serverStatus.Failed(); UpdateStatus(); throw; }
    }
    private void UpdateServerControls()
    {
        var active = ServerActive;
        ServerStartButton.Label = active ? "llama-server を終了" : "llama-server を起動";
        var symbol = active ? Symbol.Stop : Symbol.Play;
        if (ServerStartButton.Icon is SymbolIcon icon && icon.Symbol != symbol) icon.Symbol = symbol;
        ServerStartButton.IsEnabled = !active || runner.IsRunning("llama-server");
        LlamaStatusButton.IsEnabled = ServerStartButton.IsEnabled;
        var statusAction = active ? "llama-server を停止" : "llama-server を起動して接続";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(LlamaStatusButton, statusAction);
        ToolTipService.SetToolTip(LlamaStatusButton, active && !runner.IsRunning("llama-server")
            ? "このアプリで起動したサーバーだけ終了できます。" : statusAction);
        ToolTipService.SetToolTip(ServerStartButton, active && !runner.IsRunning("llama-server")
            ? "このアプリで起動したサーバーだけ終了できます。" : null);
        ToolTipService.SetToolTip(ServerSaveButton, active ? "保存した設定は次回起動時に反映されます。" : "設定を保存します。");
        foreach (var input in serverLaunchInputs) input.IsEnabled = !active;
    }
    private void UpdateStatus()
    {
        if (closing) return;
        UpdateServerControls();
        foreach (var button in cliButtons) button.IsEnabled = serverStatus.IsConnected;
        UpdateWebControls();
        UpdateChatControls();
        SetConnectionStatus(LlamaStatusPrefix, LlamaStatusState, LlamaStatusDetail, "llama-server", serverStatus.Spinner, serverStatus.Message);
        SetConnectionStatus(WebStatusPrefix, WebStatusState, WebStatusDetail, "Open WebUI", webStatus.Spinner, webStatus.Message);
        var starting = serverStatus.IsStarting || webStatus.IsStarting;
        var interval = TimeSpan.FromSeconds(starting ? 1 : 5);
        if (timer.Interval != interval) timer.Interval = interval;
        if (starting && !spinnerTimer.IsEnabled) spinnerTimer.Start();
        else if (!starting) spinnerTimer.Stop();
    }
    private static void SetConnectionStatus(Microsoft.UI.Xaml.Documents.Run prefix, Microsoft.UI.Xaml.Documents.Run state,
        Microsoft.UI.Xaml.Documents.Run detail, string service, string spinner, string message)
    {
        prefix.Text = $"{spinner}{service}: ";
        var keyword = message.StartsWith("接続済み", StringComparison.Ordinal) ? "接続済み" : message == "未接続" ? "未接続" : "";
        state.Text = keyword;
        detail.Text = message[keyword.Length..];
        var resource = keyword == "接続済み" ? "SystemFillColorSuccessBrush" : keyword == "未接続" ? "SystemFillColorCriticalBrush" : "TextFillColorSecondaryBrush";
        state.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[resource];
    }
    private void UpdateWebControls()
    {
        Browser.Visibility = webStatus.IsConnected ? Visibility.Visible : Visibility.Collapsed;
        StartWebButton.IsEnabled = serverStatus.IsConnected && !webStatus.IsStarting && !webStatus.IsConnected;
        StopWebButton.IsEnabled = runner.IsRunning("Open WebUI");
        ReloadWebButton.IsEnabled = webStatus.IsConnected;
        ExternalWebButton.IsEnabled = webStatus.IsConnected;
    }
    private void StartWeb()
    {
        if (!serverStatus.IsConnected) return;
        if (webStatus.IsStarting || runner.IsRunning("Open WebUI")) return;
        if (webStatus.IsConnected) return;
        webStatus.Begin(); loadedPort = 0; UpdateStatus();
        try { StartScript("Open WebUI", "Start-OpenWebUI.ps1", new() { ["Port"] = settings.WebPort.ToString(CultureInfo.InvariantCulture) }); }
        catch { webStatus.Failed(); UpdateStatus(); throw; }
    }
    private async Task PickCliDirectoryAsync(TextBox input)
    {
        try {
            var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            if (folder != null && !closing) {
                input.Text = folder.Path;
                settings.CliWorkingDirectory = folder.Path;
                settings.Save();
            }
        } catch (Exception e) { if (!closing) ShowError("フォルダーを選択できません: " + e.Message); }
    }
    private void OpenCli(string script, bool capture, string directory)
    {
        if (!serverStatus.IsConnected) return;
        // CLI launchers and their proxy dependencies are shipped with this GUI.
        // Never resolve a CLI PowerShell script from the backend folder setting.
        if (script is not ("Start-LocalClaude.ps1" or "Start-LocalCodex.ps1")) throw new ArgumentException("未対応の CLI です。");
        var localScript = Path.Combine(AppContext.BaseDirectory, "Cli", script);
        if (!File.Exists(localScript)) throw new FileNotFoundException("同梱の CLI 起動スクリプトが見つかりません。GUI を再ビルドしてください。", localScript);
        var workingDirectory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(
            string.IsNullOrWhiteSpace(directory) ? settings.RepositoryRoot : directory.Trim()), settings.RepositoryRoot);
        if (!Directory.Exists(workingDirectory)) throw new DirectoryNotFoundException("作業ディレクトリが見つかりません: " + workingDirectory);
        settings.CliWorkingDirectory = workingDirectory;
        settings.Save();
        var terminalAlias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        var info = new ProcessStartInfo(File.Exists(terminalAlias) ? terminalAlias : "wt.exe") { WorkingDirectory = workingDirectory, UseShellExecute = true };
        foreach (var arg in new[] { "-w", "new", "new-tab", "--title", script == "Start-LocalClaude.ps1" ? "Claude Code" : "Codex CLI", "--startingDirectory", workingDirectory, ProcessRunner.PowerShell }) info.ArgumentList.Add(arg);
        foreach (var arg in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-EncodedCommand", ProcessRunner.EncodedScript(localScript, capture ? new Dictionary<string, string?> { ["Capture"] = null } : new()) }) info.ArgumentList.Add(arg);
        try { Process.Start(info)?.Dispose(); }
        catch (System.ComponentModel.Win32Exception e) { throw new InvalidOperationException("Windows Terminal を開けません。インストールと wt.exe の実行エイリアスを確認してください。", e); }
    }
    private Task EnsureBrowserAsync() => !webStatus.IsConnected ? Task.CompletedTask : browserInitialization ??= InitializeBrowserAsync();
    private async Task InitializeBrowserAsync()
    {
        try {
            var env = await CoreWebView2Environment.CreateWithOptionsAsync("", Path.Combine(Settings.DataDirectory, "WebView2"), null);
            await Browser.EnsureCoreWebView2Async(env);
            Browser.CoreWebView2.NewWindowRequested += (_, args) => { args.Handled = true; Execute(() => Process.Start(new ProcessStartInfo(args.Uri) { UseShellExecute = true })?.Dispose()); };
            Browser.NavigationCompleted += (_, args) => { if (!webStatus.IsConnected) return; if (!args.IsSuccess) { Notice.Title = "Open WebUI に接続できません"; Notice.Message = "Open WebUI を起動し、準備が完了するまでお待ちください。接続できると自動で表示します。"; Notice.Severity = InfoBarSeverity.Informational; Notice.IsOpen = true; loadedPort = 0; } else Notice.IsOpen = false; };
            if (webStatus.IsConnected) { Browser.Source = new Uri(WebUrl); loadedPort = settings.WebPort; }
        } catch (Exception e) { browserInitialization = null; ShowError("WebView2 を初期化できません。WebView2 Runtime を確認してください。 " + e.Message); }
    }
    private async Task RefreshStatusAsync()
    {
        if (checking || closing) return;
        checking = true;
        try {
            var serverTask = ProbeAsync("http://127.0.0.1:9931/props");
            var webTask = ProbeAsync(WebUrl + "/health");
            await Task.WhenAll(serverTask, webTask);
            if (closing) return;
            var server = await serverTask; var web = await webTask;
            var webJustConnected = web != null && !webStatus.IsConnected;
            webStatus.SetConnection(web != null);
            if (web == null) loadedPort = 0;
            string model = "";
            maximumChatContext = null;
            if (server != null) try { maximumChatContext = ChatContextUsage.Maximum(System.Text.Json.Nodes.JsonNode.Parse(server)); } catch (JsonException) { }
            if (server != null) try { using var props = JsonDocument.Parse(server); if (props.RootElement.TryGetProperty("model_alias", out var alias)) model = alias.GetString() ?? ""; } catch (JsonException) { }
            serverStatus.SetConnection(server != null, model);
            UpdateStatus();
            if (web != null && (page == "web" || Browser.CoreWebView2 != null)) {
                var browserAlreadyInitialized = Browser.CoreWebView2 != null;
                await EnsureBrowserAsync();
                if (webStatus.IsConnected && Browser.CoreWebView2 != null) {
                    if (loadedPort != settings.WebPort) { loadedPort = settings.WebPort; Browser.CoreWebView2.Navigate(WebUrl); }
                    else if (webJustConnected && browserAlreadyInitialized) Browser.CoreWebView2.Reload();
                }
            }
        } catch (Exception e) { if (!closing) AddLog(e.Message); }
        finally { checking = false; }
    }
    private async Task<string?> ProbeAsync(string url)
    {
        try { using var response = await http.GetAsync(url); return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : null; }
        catch { return null; }
    }
    private void OnStartWeb(object sender, RoutedEventArgs e) => Execute(StartWeb);
    private void OnStopWeb(object sender, RoutedEventArgs e) => Execute(() => runner.Stop("Open WebUI"));
    private async void OnReloadWeb(object sender, RoutedEventArgs e) { if (!webStatus.IsConnected) return; await EnsureBrowserAsync(); if (webStatus.IsConnected) Execute(() => Browser.CoreWebView2?.Navigate(WebUrl)); }
    private void OnExternalWeb(object sender, RoutedEventArgs e) { if (webStatus.IsConnected) Execute(() => Process.Start(new ProcessStartInfo(WebUrl) { UseShellExecute = true })?.Dispose()); }
}
