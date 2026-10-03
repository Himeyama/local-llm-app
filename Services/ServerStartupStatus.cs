namespace LocalLlm.Gui.Services;

internal sealed class ServerStartupStatus
{
    private const string Frames = "⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏";
    private int frame;
    private int phase;
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
        if (text.Contains("llama_model_loader") || text.Contains("load_model")) { next = 1; message = "モデル情報を確認しています…"; }
        if (text.Contains("load_tensors") || text.Contains("loading model")) { next = 2; message = "モデルを読み込んでいます…"; }
        if (text.Contains("offload") || text.Contains("model buffer")) { next = 3; message = "モデルを GPU に転送しています…"; }
        if (text.Contains("llama_context") || text.Contains("kv cache") || text.Contains("llama_kv_cache")) { next = 4; message = "コンテキストを準備しています…"; }
        if (text.Contains("warmup") || text.Contains("warm up") || text.Contains("warming up")) { next = 5; message = "ウォームアップしています…"; }
        if (text.Contains("server is listening") || text.Contains("all slots are idle")) { next = 6; message = "接続を確認しています…"; }
        // Interleaved worker logs must not move the displayed phase backwards.
        if (message != null && next >= phase) { phase = next; Message = message; }
    }
    public void SetConnection(bool connected, string model = "")
    {
        IsConnected = connected;
        if (connected) { IsStarting = false; Message = "接続済み" + (model.Length > 0 ? " · " + model : ""); }
        else if (!IsStarting) Message = "未接続";
    }
    public void Finish(int exitCode)
    {
        IsStarting = false; IsConnected = false;
        Message = exitCode == 0 ? "停止しました" : $"終了しました（コード {exitCode}）";
    }
    public void Failed() { IsStarting = false; IsConnected = false; Message = "起動に失敗しました"; }
}
