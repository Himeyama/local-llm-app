namespace LocalLlm.Gui.Services;

internal sealed class WorkspaceFileOperations
{
    public string Root { get; }
    public WorkspaceFileOperations(string root) => Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public bool IsRoot(string path) => string.Equals(Root, Full(path), StringComparison.OrdinalIgnoreCase);
    public string RelativePath(string path)
    {
        var full = Full(path);
        if (!IsWithin(Root, full)) throw new IOException("作業フォルダー外のパスです。");
        return Path.GetRelativePath(Root, full);
    }

    public string Create(string directory, string name, bool folder)
    {
        var target = NamedTarget(CheckedDirectory(directory), name);
        EnsureAbsent(target);
        if (folder) Directory.CreateDirectory(target);
        else using (new FileStream(target, FileMode.CreateNew, FileAccess.Write)) { }
        return target;
    }

    public string Rename(string path, string name)
    {
        var source = Checked(path, allowRoot: false);
        var target = NamedTarget(Path.GetDirectoryName(source)!, name);
        if (string.Equals(source, target, StringComparison.Ordinal)) return source;
        bool folder = Directory.Exists(source);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) {
            var temporary = NamedTarget(Path.GetDirectoryName(source)!, ".local-llm-rename-" + Guid.NewGuid().ToString("N"));
            Move(source, temporary, folder);
            try { Move(temporary, target, folder); }
            catch { Move(temporary, source, folder); throw; }
        }
        else { EnsureAbsent(target); Move(source, target, folder); }
        return target;
    }

    public void Delete(string path)
    {
        // Both the absolute target and every traversed entry must remain inside this workspace.
        var target = Checked(path, allowRoot: false);
        CheckTree(target);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        else File.Delete(target);
    }

    public IReadOnlyList<string> Paste(IEnumerable<string> paths, string directory, bool cut)
    {
        var destination = CheckedDirectory(directory);
        var sources = paths.Select(CheckedSource).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sources.Count == 0) throw new IOException("貼り付けるファイルがありません。");
        var targets = sources.Select(source => NamedTarget(destination, Path.GetFileName(source))).ToList();
        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
            throw new IOException("同じ名前の項目が複数含まれています。");
        for (int index = 0; index < sources.Count; index++) {
            var source = sources[index];
            if (Directory.Exists(source) && IsWithin(source, destination))
                throw new IOException("フォルダー自身やその配下には貼り付けできません。");
            if (sources.Any(other => !string.Equals(source, other, StringComparison.OrdinalIgnoreCase) && IsWithin(source, other)))
                throw new IOException("親フォルダーとその中の項目を同時に貼り付けできません。");
            EnsureAbsent(targets[index]);
            CheckSourceTree(source);
        }
        for (int index = 0; index < sources.Count; index++) {
            var source = sources[index]; var target = targets[index];
            if (cut && Directory.Exists(source) && !string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase)) {
                CopyStaged(source, target);
                // Cross-volume moves delete only the absolute source explicitly supplied by the clipboard.
                CheckSourceTree(source);
                Directory.Delete(source, recursive: true);
            }
            else if (cut) Move(source, target, Directory.Exists(source));
            else CopyStaged(source, target);
        }
        return targets;
    }

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.EndsWith(' ') || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\'))
            throw new ArgumentException("名前にはパスや使用できない文字を含めないでください。");
        var stem = name.Split('.')[0].TrimEnd(' ');
        if (new[] { "CON", "PRN", "AUX", "NUL", "CLOCK$", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
            throw new ArgumentException("Windows の予約名は使用できません。");
    }

    private string NamedTarget(string directory, string name)
    {
        ValidateName(name);
        return Checked(Path.Combine(directory, name), allowMissing: true, allowRoot: false);
    }

    private string CheckedDirectory(string path)
    {
        var directory = Checked(path);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("貼り付け先・作成先のフォルダーが見つかりません。");
        return directory;
    }

    private string Checked(string path, bool allowMissing = false, bool allowRoot = true)
    {
        var full = Full(path);
        if (!IsWithin(Root, full)) throw new IOException("作業フォルダー外の項目は操作できません。");
        if (!allowRoot && IsRoot(full)) throw new IOException("作業フォルダー自体は変更できません。");
        for (string? current = full; current != null && IsWithin(Root, current); current = Path.GetDirectoryName(current)) {
            try {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                    throw new IOException("リンク・ジャンクションを経由するファイル操作には対応していません。");
            }
            catch (FileNotFoundException) when (allowMissing && string.Equals(current, full, StringComparison.OrdinalIgnoreCase)) { }
            catch (DirectoryNotFoundException) when (allowMissing && string.Equals(current, full, StringComparison.OrdinalIgnoreCase)) { }
        }
        return full;
    }

    private void CheckTree(string path)
    {
        Checked(path);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckTree(child);
    }

    private string CheckedSource(string path)
    {
        var full = Full(path);
        if (IsRoot(full) || string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
            throw new IOException("作業フォルダー自体やドライブ全体は切り取り・コピーできません。");
        CheckReadPath(full);
        return full;
    }

    private static void CheckReadPath(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current)) {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("リンク・ジャンクションを経由するファイル操作には対応していません。");
        }
    }

    private static void CheckSourceTree(string path)
    {
        CheckReadPath(path);
        if (Directory.Exists(path)) foreach (var child in Directory.EnumerateFileSystemEntries(path)) CheckSourceTree(child);
    }

    private void CopyStaged(string source, string target)
    {
        var stage = NamedTarget(Path.GetDirectoryName(target)!, ".local-llm-copy-" + Guid.NewGuid().ToString("N"));
        try {
            CopyTree(source, stage);
            Move(stage, target, Directory.Exists(stage));
        }
        finally {
            if (File.Exists(stage) || Directory.Exists(stage)) Delete(stage);
        }
    }

    private void CopyTree(string source, string target)
    {
        CheckReadPath(source);
        if (!Directory.Exists(source)) { File.Copy(source, target, overwrite: false); return; }
        EnsureAbsent(target);
        Directory.CreateDirectory(target);
        foreach (var child in Directory.EnumerateFileSystemEntries(source)) CopyTree(child, Path.Combine(target, Path.GetFileName(child)));
    }

    private static void Move(string source, string target, bool folder)
    {
        if (folder) Directory.Move(source, target);
        else File.Move(source, target, overwrite: false);
    }

    private static void EnsureAbsent(string path)
    {
        if (Path.Exists(path)) throw new IOException("同じ名前のファイルまたはフォルダーが既に存在します。");
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool IsWithin(string parent, string path) => string.Equals(parent, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
