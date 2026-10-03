namespace LocalLlm.Gui.Services;

internal sealed class WebStartupStatus
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private int frame, phase;
    public bool IsStarting { get; private set; }
    public bool IsConnected { get; private set; }
    public string Message { get; private set; } = "確認中";
    public string Spinner => IsStarting ? Frames[frame].ToString() + " " : "";

    public void Begin()
    {
        IsStarting = true; IsConnected = false; frame = 0; phase = 0;
        Message = "起動しています…";
    }
    public void Tick() { if (IsStarting) frame = (frame + 1) % Frames.Length; }
    public void ObserveLog(string line)
    {
        if (!IsStarting) return;
        var text = line.ToLowerInvariant();
        var next = 0;
        string? message = null;
        if (text.Contains("webui_secret_key") || text.Contains("loading config")) { next = 1; message = "設定を読み込んでいます…"; }
        if (text.Contains("alembic") || text.Contains("migration") || text.Contains("running upgrade")) { next = 2; message = "データベースを準備しています…"; }
        if (text.Contains("embedding model") || text.Contains("sentence_transformers") || text.Contains("fetching") || text.Contains("downloading")) { next = 3; message = "検索用モデルを準備しています…"; }
        if (text.Contains("started server process") || text.Contains("waiting for application startup")) { next = 4; message = "アプリケーションを初期化しています…"; }
        if (text.Contains("application startup complete") || text.Contains("uvicorn running on")) { next = 5; message = "接続を確認しています…"; }
        if (message != null && next >= phase) { phase = next; Message = message; }
    }
    public void SetConnection(bool connected)
    {
        IsConnected = connected;
        if (connected) { IsStarting = false; Message = "接続済み"; }
        else if (!IsStarting) Message = "未接続";
    }
    public void Finish(int exitCode, bool stopRequested = false)
    {
        IsStarting = false; IsConnected = false;
        Message = stopRequested ? "未接続" : exitCode == 0 ? "停止しました" : $"終了しました（コード {exitCode}）";
    }
    public void Failed() { IsStarting = false; IsConnected = false; Message = "起動に失敗しました"; }
}
