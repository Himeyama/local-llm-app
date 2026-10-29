namespace LocalLlm.Gui.Services;

public sealed record WorkspaceEntry(string Name, string Path, bool IsDirectory, bool IsLink)
{
    public bool CanExpand => IsDirectory && !IsLink;
    public override string ToString() => Name;
}

internal static class WorkspaceFiles
{
    public static IReadOnlyList<WorkspaceEntry> ReadDirectory(string path)
    {
        var entries = new List<WorkspaceEntry>();
        foreach (var item in new DirectoryInfo(path).EnumerateFileSystemInfos()) {
            try {
                var attributes = item.Attributes;
                entries.Add(new WorkspaceEntry(item.Name, item.FullName,
                    attributes.HasFlag(FileAttributes.Directory), attributes.HasFlag(FileAttributes.ReparsePoint)));
            }
            catch (FileNotFoundException) { } // A file can disappear while enumerating.
            catch (DirectoryNotFoundException) { }
        }
        return entries.OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
