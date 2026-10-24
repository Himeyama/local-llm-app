using System.Text.Json;
using LocalLlm.Gui.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace LocalLlm.Gui;

public sealed partial class MainWindow
{
    private readonly InvestigationStore investigationStore = new(Path.Combine(Settings.DataDirectory, "investigations"));
    private readonly HttpClient investigationModel = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly HttpClient investigationProbe = InvestigationRunner.CreateProbeClient();
    private List<Investigation> investigations = [];
    private Investigation? investigation;
    private CancellationTokenSource? investigationCancellation;
    private bool selectingInvestigation;

    private void InitializeInvestigation()
    {
        try { investigations = investigationStore.Load(); RefreshInvestigationHistory(); }
        catch (Exception ex) { ShowError("調査履歴を読み込めません: " + ex.Message); }
        Closed += (_, _) => { investigationCancellation?.Cancel(); investigationModel.Dispose(); investigationProbe.Dispose(); };
        RenderInvestigation();
    }

    private void OnOpenInvestigation(object sender, RoutedEventArgs e)
    {
        page = "investigation"; BuildPage();
    }

    private void RefreshInvestigationHistory()
    {
        selectingInvestigation = true;
        InvestigationHistory.ItemsSource = null;
        InvestigationHistory.ItemsSource = investigations.OrderByDescending(r => r.UpdatedAt).ToList();
        InvestigationHistory.SelectedItem = investigation;
        selectingInvestigation = false;
    }

    private void OnInvestigationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (selectingInvestigation || investigationCancellation != null || InvestigationHistory.SelectedItem is not Investigation selected) return;
        investigation = selected;
        InvestigationTarget.Text = selected.Target;
        InvestigationExcluded.Text = string.Join(Environment.NewLine, selected.ExcludedPaths);
        InvestigationRestrictions.Text = selected.Restrictions;
        InvestigationRequestLimit.Value = selected.RequestLimit;
        InvestigationRoundLimit.Value = selected.RoundLimit;
        InvestigationAuthorized.IsChecked = false;
        InvestigationSettings.IsExpanded = false;
        InvestigationInput.Text = "";
        RenderInvestigation();
    }

    private void OnNewInvestigation(object sender, RoutedEventArgs e)
    {
        if (investigationCancellation != null) return;
        investigation = null;
        InvestigationAuthorized.IsChecked = false;
        InvestigationInput.Text = "";
        InvestigationSettings.IsExpanded = true;
        RefreshInvestigationHistory(); RenderInvestigation();
    }

    private async void OnStartInvestigation(object sender, RoutedEventArgs e) => await StartInvestigationAsync();
    private async void OnInvestigationKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter || !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await StartInvestigationAsync();
    }
    private void OnStopInvestigation(object sender, RoutedEventArgs e) => investigationCancellation?.Cancel();

    private async Task StartInvestigationAsync()
    {
        if (investigationCancellation != null) return;
        try
        {
            if (InvestigationAuthorized.IsChecked != true) throw new ArgumentException("対象の診断権限を確認し、対象範囲のチェックをオンにしてください。");
            var text = InvestigationInput.Text.Trim();
            if (text.Length == 0) throw new ArgumentException("調査目的または追加の指示を入力してください。");
            if (investigation == null)
            {
                var candidate = new Investigation
                {
                    Target = InvestigationTarget.Text.Trim(), Goal = text,
                    Restrictions = InvestigationRestrictions.Text.Trim(),
                    ExcludedPaths = InvestigationExcluded.Text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries),
                    RequestLimit = double.IsFinite(InvestigationRequestLimit.Value) ? (int)InvestigationRequestLimit.Value : 24,
                    RoundLimit = double.IsFinite(InvestigationRoundLimit.Value) ? (int)InvestigationRoundLimit.Value : 4
                };
                _ = new InvestigationScope(candidate);
                investigation = candidate; investigations.Add(candidate);
            }
            investigation.Messages.Add(new("user", text));
            InvestigationInput.Text = "";
            InvestigationSettings.IsExpanded = false;
            investigationStore.Save(investigation);
            await ExecuteInvestigationAsync((runner, run, changed, ct) => runner.RunAsync(run, changed, ct, UpdateInvestigationLiveReasoning));
        }
        catch (Exception ex) { if (!closing) ShowError("診断を開始できません: " + ex.Message); }
    }

    private async Task ExecuteInvestigationAsync(Func<InvestigationRunner, Investigation, Action, CancellationToken, Task> action)
    {
        if (investigationCancellation != null || investigation == null) return;
        var run = investigation;
        using var cancellation = new CancellationTokenSource();
        investigationCancellation = cancellation;
        RenderInvestigation();
        void Changed()
        {
            investigationStore.Save(run);
            if (!closing) RenderInvestigation();
        }
        try { await action(new(investigationModel, investigationProbe), run, Changed, cancellation.Token); }
        catch (OperationCanceledException) { run.Messages.Add(new("system", "停止しました。完了済みの証拠は保存されています。")); }
        catch (Exception ex) { run.Messages.Add(new("system", "実行できません: " + ex.Message)); if (!closing) ShowError("診断処理: " + ex.Message); }
        finally
        {
            investigationCancellation = null;
            UpdateInvestigationLiveReasoning("");
            try { investigationStore.Save(run); } catch (Exception ex) { if (!closing) ShowError("調査を保存できません: " + ex.Message); }
            if (!closing) { RefreshInvestigationHistory(); RenderInvestigation(); }
        }
    }

    private static TextBlock InvestigationText(string text, double size = 14) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true
    };

    private void OnInvestigationInputGotFocus(object sender, RoutedEventArgs e) => InvestigationComposer.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
    private void OnInvestigationInputLostFocus(object sender, RoutedEventArgs e) => InvestigationComposer.BorderBrush = (Brush)Application.Current.Resources["TextControlBorderBrush"];

    private void UpdateInvestigationLiveReasoning(string text)
    {
        if (closing) return;
        InvestigationLiveReasoning.Text = text;
        InvestigationLiveReasoning.Visibility = settings.InvestigationShowReasoning && text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnToggleInvestigationReasoningVisibility(object sender, RoutedEventArgs e)
    {
        var previous = settings.InvestigationShowReasoning;
        try { settings.InvestigationShowReasoning = InvestigationReasoningVisibilityButton.IsChecked == true; settings.Save(); }
        catch (Exception ex) { settings.InvestigationShowReasoning = previous; ShowError(ex.Message); }
        RenderInvestigation();
    }

    private void RenderInvestigation()
    {
        if (closing) return;
        bool busy = investigationCancellation != null;
        InvestigationReasoningVisibilityButton.IsChecked = settings.InvestigationShowReasoning;
        var reasoningLabel = settings.InvestigationShowReasoning ? "推論内容を非表示にする" : "推論内容を表示する";
        ToolTipService.SetToolTip(InvestigationReasoningVisibilityButton, reasoningLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(InvestigationReasoningVisibilityButton, reasoningLabel);
        UpdateInvestigationLiveReasoning(InvestigationLiveReasoning.Text);
        InvestigationHistory.IsEnabled = NewInvestigationButton.IsEnabled = !busy;
        InvestigationTarget.IsEnabled = InvestigationExcluded.IsEnabled = InvestigationRestrictions.IsEnabled = investigation == null && !busy;
        InvestigationRequestLimit.IsEnabled = InvestigationRoundLimit.IsEnabled = investigation == null && !busy;
        InvestigationAuthorized.IsEnabled = !busy;
        StartInvestigationButton.IsEnabled = InvestigationInput.IsEnabled = !busy;
        StopInvestigationButton.IsEnabled = busy;
        StopInvestigationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        InvestigationStatus.Text = investigation == null ? "対象と調査目的を設定してください。検出事項は確定前の候補として表示します。" :
            $"{investigation.Status} ｜ 通信 {investigation.Evidence.Count(e => !e.Retest)}/{investigation.RequestLimit} ｜ 要確認 {investigation.Findings.Count} ｜ 再テスト {investigation.Evidence.Count(e => e.Retest)}";
        InvestigationMessages.Children.Clear(); InvestigationFindings.Children.Clear(); InvestigationEvidence.Children.Clear(); InvestigationGraph.Children.Clear();
        if (investigation == null)
        {
            InvestigationMessages.Children.Add(InvestigationText("脆弱性診断モード", 24));
            InvestigationMessages.Children.Add(InvestigationText("対象の棚卸し → 仮説 → HTTPで確認 → 再計画 → 修正後の再テスト\n\nローカルAIが過去の検査と通信記録を読み、次の検査を提案します。検出・再テスト、通信証拠、資産・探索グラフを各タブで確認できます。"));
            return;
        }
        foreach (var message in investigation.Messages)
        {
            var content = new StackPanel { Spacing = 5 };
            content.Children.Add(InvestigationText(message.Role switch { "user" => "あなた", "assistant" => "診断プランナー", _ => "実行状況" }, 12));
            if (settings.InvestigationShowReasoning && !string.IsNullOrEmpty(message.Reasoning)) content.Children.Add(RenderReasoning(message.Reasoning));
            content.Children.Add(InvestigationText(message.Text));
            InvestigationMessages.Children.Add(new Border
            {
                Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(12),
                HorizontalAlignment = message.Role == "user" ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
                Background = (Brush)Application.Current.Resources[message.Role == "user" ? "AccentFillColorDefaultBrush" : "ControlFillColorSecondaryBrush"]
            });
            if (message.Role == "user") foreach (var block in content.Children.OfType<TextBlock>()) block.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);
        }
        if (investigation.Findings.Count == 0) InvestigationFindings.Children.Add(InvestigationText("検出事項はまだありません。未検出は安全性を保証しません。"));
        foreach (var finding in investigation.Findings)
        {
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(InvestigationText(finding.Title, 18));
            panel.Children.Add(InvestigationText($"{finding.Status} · 証拠 {finding.EvidenceId}\n検査 {finding.TaskId}\n候補の証拠条件を再確認します。アプリ全体の安全性・修正完了を保証する判定ではありません。"));
            foreach (var retest in finding.Retests) panel.Children.Add(InvestigationText($"{retest.At.ToLocalTime():g}  {retest.Verdict}\n証拠 {retest.EvidenceId}", 12));
            var button = new Button { Content = "独立した再テスト（新しいHTTP通信）", IsEnabled = !busy };
            button.Click += async (_, _) =>
            {
                if (InvestigationAuthorized.IsChecked != true) { ShowError("再テスト前に対象の診断権限を確認してください。"); InvestigationSettings.IsExpanded = true; return; }
                await ExecuteInvestigationAsync((runner, run, changed, ct) => runner.RetestAsync(run, finding, changed, ct));
            };
            panel.Children.Add(button);
            InvestigationFindings.Children.Add(new Border { Child = panel, Padding = new Thickness(12), BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"], CornerRadius = new CornerRadius(8) });
        }
        foreach (var evidence in investigation.Evidence.AsEnumerable().Reverse())
        {
            var details = $"証拠ID: {evidence.Id}\n検査ID: {evidence.TaskId}\n取得: {evidence.At.ToLocalTime():O}\n{evidence.Method} {evidence.Url}\nHTTP {evidence.StatusCode}\n{evidence.Error}\n" +
                string.Join("\n", evidence.Headers.Select(h => h.Key + ": " + h.Value)) + $"\n\n{evidence.Body}\n\n本文省略: {evidence.Truncated}";
            InvestigationEvidence.Children.Add(new Expander
            {
                Header = InvestigationText($"{(evidence.Retest ? "再テスト · " : "")}{evidence.Method} {evidence.StatusCode} {evidence.Url}"),
                Content = InvestigationText(details, 12), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
            });
        }
        InvestigationGraph.Children.Add(InvestigationText("資産グラフ", 20));
        foreach (var asset in investigation.Assets) InvestigationGraph.Children.Add(InvestigationText($"[{asset.Kind}] {asset.Name}\nID: {asset.Id}", 12));
        InvestigationGraph.Children.Add(InvestigationText("探索グラフ・作業状態", 20));
        foreach (var task in investigation.Tasks) InvestigationGraph.Children.Add(InvestigationText($"計画 {task.Round} · {task.Status} · {task.Method} {task.Url}\n{task.Hypothesis}\n条件: {task.Check} {task.Value}\nID: {task.Id}", 12));
        var graph = new TextBox { PlaceholderText = "ID・URL・関係を検索", Header = "関連（始点 → 関係 → 終点）" };
        var edges = InvestigationText(string.Join("\n", investigation.Edges.Select(e => $"{e.From} → {e.Relation} → {e.To}")), 12);
        graph.TextChanged += (_, _) => edges.Text = string.Join("\n", investigation.Edges.Where(e => JsonSerializer.Serialize(e).Contains(graph.Text, StringComparison.OrdinalIgnoreCase)).Select(e => $"{e.From} → {e.Relation} → {e.To}"));
        InvestigationGraph.Children.Add(graph); InvestigationGraph.Children.Add(edges);
    }
}
