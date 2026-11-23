namespace LocalLlm.Gui.Services;

public static class BrowserFiles
{
    public static bool IsHtml(string path) => Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm";

    public static bool CanPreview(string path) => IsHtml(path) || Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".mp4" or ".m4v" or ".webm" or ".ogv" or ".mov" or ".avi" or ".mkv" or ".wmv" or
        ".mp3" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".oga" or ".flac" or ".opus" or ".wma";
}
