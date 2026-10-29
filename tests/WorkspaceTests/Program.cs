using LocalLlm.Gui.Services;

var parent = Path.GetFullPath(AppContext.BaseDirectory);
var root = Path.Combine(parent, "workspace-files-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try {
    Directory.CreateDirectory(Path.Combine(root, "zFolder"));
    Directory.CreateDirectory(Path.Combine(root, "aFolder"));
    File.WriteAllText(Path.Combine(root, "zFolder", "nested.txt"), "nested");
    File.WriteAllText(Path.Combine(root, "日本語.txt"), "日本語");
    File.WriteAllText(Path.Combine(root, "B.txt"), "test");
    File.WriteAllText(Path.Combine(root, ".hidden"), "hidden");
    var entries = WorkspaceFiles.ReadDirectory(root);
    Check(entries.Count == 5, "Only immediate children are loaded");
    Check(entries.Take(2).All(entry => entry.IsDirectory && entry.CanExpand), "Folders come before files");
    Check(entries[0].Name == "aFolder", "Folders are sorted by name");
    Check(entries.Any(entry => entry.Name == "日本語.txt" && !entry.IsDirectory && !entry.CanExpand), "Unicode files are represented as leaves");
    var unicodeFile = entries.Single(entry => entry.Name == "日本語.txt");
    Check(unicodeFile.ToString() == "日本語.txt", "A default node renderer must show its filename, never a UI or model type name");
    Check(entries.Any(entry => entry.Name == ".hidden"), "Dotfiles remain visible");
    Check(entries.All(entry => Path.GetDirectoryName(entry.Path) == root), "Nodes retain absolute paths for their workspace");
    Check(WorkspaceFiles.ReadDirectory(Path.Combine(root, "aFolder")).Count == 0, "Empty folders can be expanded");
    try { WorkspaceFiles.ReadDirectory(Path.Combine(root, "missing")); throw new Exception("Missing folders must report an error"); }
    catch (DirectoryNotFoundException) { }
    var operations = new WorkspaceFileOperations(root);
    var source = operations.Create(root, "source", true);
    var nested = operations.Create(source, "nested", true);
    var original = operations.Create(nested, "日本語.txt", false);
    File.WriteAllText(original, "保持される内容");
    var destination = operations.Create(root, "destination", true);
    var copied = operations.Paste(new[] { source }, destination, cut: false).Single();
    Check(File.ReadAllText(Path.Combine(copied, "nested", "日本語.txt")) == "保持される内容", "Folder copy preserves nested file content");
    Check(File.Exists(original), "Copy leaves its source intact");
    var renamed = operations.Rename(Path.Combine(copied, "nested", "日本語.txt"), "renamed.txt");
    renamed = operations.Rename(renamed, "RENAMED.txt");
    Check(Directory.EnumerateFiles(Path.GetDirectoryName(renamed)!).Any(path => Path.GetFileName(path) == "RENAMED.txt"), "Case-only rename updates the actual filename");
    Check(operations.RelativePath(original) == Path.Combine("source", "nested", "日本語.txt"), "Relative paths use the workspace root");
    Check(operations.RelativePath(root) == ".", "The root has a valid relative path");

    var moved = operations.Paste(new[] { original }, destination, cut: true).Single();
    Check(!File.Exists(original) && File.ReadAllText(moved) == "保持される内容", "Cut and paste moves the file without changing its content");
    var moveFolder = operations.Create(root, "move-folder", true);
    var movedFolder = operations.Paste(new[] { moveFolder }, destination, cut: true).Single();
    Check(!Directory.Exists(moveFolder) && Directory.Exists(movedFolder), "Cut and paste supports directories");
    var conflict = Path.Combine(root, "日本語.txt");
    File.WriteAllText(conflict, "do not overwrite");
    var untouched = operations.Create(root, "untouched.txt", false);
    ExpectRejected(() => operations.Paste(new[] { untouched, conflict }, destination, cut: true));
    Check(File.Exists(untouched) && File.Exists(conflict), "Collision preflight prevents partial moves");
    Check(File.ReadAllText(moved) == "保持される内容", "A conflicting paste cannot overwrite the existing file");
    var receivingWorkspace = operations.Create(root, "receiving-workspace", true);
    var receivingOperations = new WorkspaceFileOperations(receivingWorkspace);
    var externalCopy = receivingOperations.Paste(new[] { conflict }, receivingWorkspace, cut: false).Single();
    Check(File.ReadAllText(externalCopy) == "do not overwrite" && File.Exists(conflict), "Clipboard copies can come from a different workspace");
    var externalMoveSource = operations.Create(root, "external-move.txt", false);
    var externalMove = receivingOperations.Paste(new[] { externalMoveSource }, receivingWorkspace, cut: true).Single();
    Check(!File.Exists(externalMoveSource) && File.Exists(externalMove), "Explicit clipboard cuts can move between workspaces");
    ExpectRejected(() => receivingOperations.Paste(new[] { source }, root, cut: false));
    ExpectRejected(() => operations.Paste(new[] { source }, nested, cut: false));
    ExpectRejected(() => operations.Paste(new[] { source, nested }, destination, cut: false));
    ExpectRejected(() => operations.Rename(conflict, "untouched.txt"));
    ExpectRejected(() => operations.Delete(root));
    ExpectRejected(() => operations.Rename(root, "renamed-root"));
    ExpectRejected(() => operations.Delete(root + "-sibling"));
    ExpectRejected(() => operations.Create(Path.Combine(root, ".."), "outside.txt", false));
    foreach (var invalidName in new[] { "..", "../escape.txt", "child\\escape.txt", "CON", "NUL.txt", "trailing.", "trailing ", "file:stream" })
        ExpectRejected(() => operations.Create(root, invalidName, false));
    operations.Delete(copied);
    Check(!Directory.Exists(copied) && Directory.Exists(source), "Deleting a copied directory leaves the original intact");
    operations.Delete(untouched);
    Check(!File.Exists(untouched), "File deletion removes only its selected file");
    Check(!Directory.EnumerateFileSystemEntries(root, ".local-llm-*", SearchOption.AllDirectories).Any(), "Copy and rename staging paths are cleaned up");
    Console.WriteLine("PASS: tree metadata; create, rename, copy, cut/paste, deletion, relative paths; collision, traversal and recursive-copy protection");
}
finally {
    // The recursively removed directory must be the unique test directory created above.
    if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(parent), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Test cleanup escaped its output directory");
    Directory.Delete(root, true);
}

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void ExpectRejected(Action action)
{
    try { action(); }
    catch (IOException) { return; }
    catch (ArgumentException) { return; }
    throw new Exception("An invalid operation was accepted");
}
