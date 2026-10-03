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
    private string page = "server";
    private int loadedPort;
    private Task? browserInitialization;
    private Button? serverStopButton;
    private Button? serverSettingsButton;
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
        runner.Exited += (name, code) => DispatcherQueue.TryEnqueue(() => {
            if (closing) return;
            if (name == "llama-server") serverStatus.Finish(code);
            else if (name == "Open WebUI") { webStatus.Finish(code); loadedPort = 0; }
            else return;
            UpdateStatus();
            if (code != 0) ShowError($"{name} が終了しました（コード {code}）。ログを確認してください。");
        });
        timer.Tick += async (_, _) => await RefreshStatusAsync();
        spinnerTimer.Tick += (_, _) => { serverStatus.Tick(); webStatus.Tick(); UpdateStatus(); };
        Closed += (_, _) => { closing = true; timer.Stop(); spinnerTimer.Stop(); runner.Dispose(); http.Dispose(); Browser.Close(); };
        ready = true;
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
            Navigation.IsPaneToggleButtonVisible = true;
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

    private void OnPaneToggleRequested(TitleBar sender, object args) => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

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
    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!ready || args.SelectedItem is not NavigationViewItem item) return;
        page = item.Tag.ToString()!;
        PageTitle.Text = item.Content.ToString();
        AppTitleBar.Subtitle = item.Content.ToString();
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
    private static int Read(NumberBox box)
    {
        if (double.IsNaN(box.Value) || box.Value != Math.Truncate(box.Value) || box.Value < box.Minimum || box.Value > box.Maximum)
            throw new ArgumentException($"{(box.Header as TextBlock)?.Text} に範囲内の整数を指定してください。");
        return checked((int)box.Value);
    }
    private void OnFormSizeChanged(object sender, SizeChangedEventArgs args)
    {
        Form.Width = Math.Max(0, Math.Min(920, args.NewSize.Width - 16));
        foreach (var number in Form.Children.OfType<NumberBox>()) number.Width = Math.Min(280, Form.Width);
    }

    private Button Button(string label, Action action)
    {
        var button = new Button { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, HorizontalAlignment = HorizontalAlignment.Left }; button.Click += (_, _) => Execute(action); Form.Children.Add(button); return button;
    }
    private void BuildPage()
    {
        serverStopButton = null;
        serverSettingsButton = null;
        serverLaunchInputs = Array.Empty<TextBox>();
        cliButtons = Array.Empty<Button>();
        Form.Children.Clear();
        FormScroll.Visibility = page is "web" or "logs" ? Visibility.Collapsed : Visibility.Visible;
        WebPanel.Visibility = page == "web" ? Visibility.Visible : Visibility.Collapsed;
        LogView.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        UpdateWebControls();
        switch (page)
        {
            case "web": _ = EnsureBrowserAsync(); break;
            case "server":
                var context = Number("コンテキスト長", settings.ContextSize, 65536, 1000000);
                var threads = Number("CPU スレッド", settings.Threads, 1, 512);
                var mtp = Number("MTP draft tokens（無効: 0）", settings.MtpDraftTokens, 0, 16);
                var model = Input("モデルの相対パス", Settings.DefaultModelPath);
                var mmproj = Input("対応する mmproj の相対パス", Settings.DefaultMmprojPath);
                var exe = Input("llama-server.exe の相対パス", Settings.DefaultServerExe);
                serverLaunchInputs = new[] { model, mmproj, exe };
                serverSettingsButton = Button("この設定で起動", () => {
                    var saveOnly = ServerActive;
                    var args = new Dictionary<string, string?>();
                    if (!saveOnly) {
                        if (!string.IsNullOrWhiteSpace(model.Text)) { RequireRelative(model.Text); args["ModelPath"] = model.Text; }
                        if (!string.IsNullOrWhiteSpace(mmproj.Text)) { RequireRelative(mmproj.Text); args["MmprojPath"] = mmproj.Text; }
                        if (!string.IsNullOrWhiteSpace(exe.Text)) { RequireRelative(exe.Text); args["ServerExe"] = exe.Text; }
                    }
                    settings.ContextSize = Read(context); settings.Threads = Read(threads); settings.MtpDraftTokens = Read(mtp); settings.Save();
                    if (!saveOnly) StartServer(args);
                });
                UpdateServerSettingsButton();
                serverStopButton = Button("llama-server を終了", () => runner.Stop("llama-server"));
                UpdateServerStopButton();
                break;
            case "cli":
                var capture = new CheckBox { Content = "mitmweb で通信をキャプチャー" }; Form.Children.Add(capture);
                cliButtons = new[] {
                    Button("Claude Code を開く", () => OpenCli("Start-LocalClaude.ps1", capture.IsChecked == true)),
                    Button("Codex CLI を開く", () => OpenCli("Start-LocalCodex.ps1", capture.IsChecked == true))
                };
                foreach (var button in cliButtons) button.IsEnabled = serverStatus.IsConnected;
                break;
            case "logs": LogView.Text = string.Join(Environment.NewLine, logs); break;
        }
    }
    private static void RequireRelative(string path)
    {
        if (Path.IsPathRooted(path) || path.Split('/', '\\').Contains("..")) throw new ArgumentException("リポジトリ内の相対パスを指定してください。");
    }
    private void UpdateServerStopButton()
    {
        if (serverStopButton != null) serverStopButton.IsEnabled = runner.IsRunning("llama-server");
    }
    private void StartServer(Dictionary<string, string?> args)
    {
        if (serverStatus.IsStarting || runner.IsRunning("llama-server")) return;
        if (serverStatus.IsConnected) return;
        args["ContextSize"] = settings.ContextSize.ToString(CultureInfo.InvariantCulture);
        args["Threads"] = settings.Threads.ToString(CultureInfo.InvariantCulture);
        args["MtpDraftTokens"] = settings.MtpDraftTokens.ToString(CultureInfo.InvariantCulture);
        serverStatus.Begin(); UpdateStatus();
        try { StartScript("llama-server", "Start-LlamaServer.ps1", args); }
        catch { serverStatus.Failed(); UpdateStatus(); throw; }
    }
    private void UpdateServerSettingsButton()
    {
        if (serverSettingsButton == null) return;
        var active = ServerActive;
        ((TextBlock)serverSettingsButton.Content).Text = active
            ? "数値設定を保存（次回起動時に反映）" : "この設定で起動";
        foreach (var input in serverLaunchInputs) input.IsEnabled = !active;
    }
    private void UpdateStatus()
    {
        if (closing) return;
        UpdateServerStopButton();
        UpdateServerSettingsButton();
        foreach (var button in cliButtons) button.IsEnabled = serverStatus.IsConnected;
        UpdateWebControls();
        Status.Text = $"{serverStatus.Spinner}llama-server: {serverStatus.Message}    |    {webStatus.Spinner}Open WebUI: {webStatus.Message}";
        var starting = serverStatus.IsStarting || webStatus.IsStarting;
        var interval = TimeSpan.FromSeconds(starting ? 1 : 5);
        if (timer.Interval != interval) timer.Interval = interval;
        if (starting && !spinnerTimer.IsEnabled) spinnerTimer.Start();
        else if (!starting) spinnerTimer.Stop();
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
    private void OpenCli(string script, bool capture)
    {
        if (!serverStatus.IsConnected) return;
        // CLI launchers and their proxy dependencies are shipped with this GUI.
        // Never resolve a CLI PowerShell script from the backend folder setting.
        if (script is not ("Start-LocalClaude.ps1" or "Start-LocalCodex.ps1")) throw new ArgumentException("未対応の CLI です。");
        var localScript = Path.Combine(AppContext.BaseDirectory, "Cli", script);
        if (!File.Exists(localScript)) throw new FileNotFoundException("同梱の CLI 起動スクリプトが見つかりません。GUI を再ビルドしてください。", localScript);
        var info = new ProcessStartInfo(ProcessRunner.PowerShell) { WorkingDirectory = settings.RepositoryRoot, UseShellExecute = true };
        foreach (var arg in new[] { "-NoProfile", "-NoExit", "-ExecutionPolicy", "Bypass", "-EncodedCommand", ProcessRunner.EncodedScript(localScript, capture ? new Dictionary<string, string?> { ["Capture"] = null } : new()) }) info.ArgumentList.Add(arg);
        Process.Start(info)?.Dispose();
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
