using System.Diagnostics;
using System.Text;

namespace LocalLlm.Gui.Services;

public sealed record GitChange(string Path, string? OriginalPath, char IndexStatus, char WorkTreeStatus, bool Conflicted = false)
{
    public bool IsStaged => !Conflicted && IndexStatus is not ('.' or '?');
    public bool IsUnstaged => Conflicted || WorkTreeStatus != '.';
    public string Status => Conflicted ? "競合" : IndexStatus == '?' ? "未追跡" : $"{IndexStatus}{WorkTreeStatus}".Replace('.', ' ');
    public string DisplayPath => OriginalPath == null ? Path : $"{OriginalPath} → {Path}";
}

public sealed record GitBranch(string Name, bool Current, bool Remote);
public sealed record GitSnapshot(string Root, string Branch, string? Upstream, bool HasHead, bool Detached,
    int Ahead, int Behind, IReadOnlyList<GitChange> Changes, IReadOnlyList<GitBranch> Branches, IReadOnlyList<string> Remotes, string HeadId = "")
{
    public bool CanPull => HasHead && Upstream != null;
    public string? PushRemote => Remotes.Contains("origin") ? "origin" : Remotes.Count == 1 ? Remotes[0] : null;
    public bool CanPush => HasHead && !Detached && (Upstream != null || PushRemote != null);
}

/// <summary>Git CLI integration. Paths and user input are always passed as separate arguments.</summary>
public sealed class GitRepository
{
    public async Task<GitSnapshot?> ReadAsync(string? workspace, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
        var probe = await RunAsync(workspace, ["rev-parse", "--show-toplevel"], cancellation, allowFailure: true);
        if (probe.Code != 0) {
            // Outside a work tree (including bare repositories) has no editable files.
            if (probe.Error.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ||
                probe.Error.Contains("must be run in a work tree", StringComparison.OrdinalIgnoreCase)) return null;
            throw new IOException(probe.Error.Trim());
        }
        var root = probe.Output.TrimEnd('\r', '\n');
        var status = await RunAsync(root, ["status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"], cancellation);
        var refs = await RunAsync(root, ["for-each-ref", "--format=%(refname)%09%(HEAD)", "refs/heads/", "refs/remotes/"], cancellation);
        var remotes = await RunAsync(root, ["remote"], cancellation);
        return ParseStatus(root, status.Output, ParseBranches(refs.Output), remotes.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static GitSnapshot ParseStatus(string root, string output, IReadOnlyList<GitBranch> branches, IReadOnlyList<string> remotes)
    {
        string head = "", oid = "", branch = ""; string? upstream = null;
        int ahead = 0, behind = 0;
        var changes = new List<GitChange>();
        var records = output.Split('\0');
        for (int i = 0; i < records.Length; i++) {
            var record = records[i];
            if (record.StartsWith("# branch.head ")) head = record[14..];
            else if (record.StartsWith("# branch.oid ")) oid = record[13..];
            else if (record.StartsWith("# branch.upstream ")) upstream = record[18..];
            else if (record.StartsWith("# branch.ab ")) {
                var counts = record[12..].Split(' ');
                if (counts.Length == 2) { int.TryParse(counts[0].TrimStart('+'), out ahead); int.TryParse(counts[1].TrimStart('-'), out behind); }
            }
            else if (record.StartsWith("? ")) changes.Add(new GitChange(record[2..], null, '?', '?'));
            else if (record.StartsWith("1 ") || record.StartsWith("2 ") || record.StartsWith("u ")) {
                int fields = record[0] == '1' ? 9 : record[0] == '2' ? 10 : 11;
                var parts = record.Split(' ', fields);
                if (parts.Length != fields || parts[1].Length != 2) throw new InvalidDataException("Git の変更一覧を読み取れません。");
                string? original = record[0] == '2' && i + 1 < records.Length ? records[++i] : null;
                changes.Add(new GitChange(parts[^1], original, parts[1][0], parts[1][1], record[0] == 'u'));
            }
        }
        bool detached = head == "(detached)";
        branch = detached ? $"HEAD ({oid[..Math.Min(7, oid.Length)]})" : head;
        return new GitSnapshot(root, branch, upstream, oid.Length > 0 && oid != "(initial)", detached, ahead, behind, changes, branches, remotes, oid);
    }

    private static IReadOnlyList<GitBranch> ParseBranches(string output) => output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.TrimEnd('\r').Split('\t')).Where(parts => parts.Length == 2)
        .Where(parts => !parts[0].EndsWith("/HEAD", StringComparison.Ordinal))
        .Select(parts => new GitBranch(parts[0].StartsWith("refs/heads/") ? parts[0][11..] : parts[0][13..], parts[1] == "*", parts[0].StartsWith("refs/remotes/")))
        .ToList();

    public Task<string> FetchAsync(GitSnapshot repository, CancellationToken cancellation = default) => CommandAsync(repository, ["fetch", "--all", "--prune"], cancellation, network: true);
    public Task<string> PullAsync(GitSnapshot repository, CancellationToken cancellation = default)
    {
        if (!repository.CanPull) throw new InvalidOperationException("プルには追跡先ブランチが必要です。");
        return CommandAsync(repository, ["pull", "--ff-only"], cancellation, network: true);
    }
    public async Task<string> PushAsync(GitSnapshot repository, CancellationToken cancellation = default)
    {
        if (!repository.CanPush) throw new InvalidOperationException("プッシュ先リモートとブランチを確認してください。");
        // Explicit refspec avoids push.default=matching pushing unrelated branches.
        if (repository.Upstream != null) {
            var remote = (await RunAsync(repository.Root, ["config", "--get", $"branch.{repository.Branch}.remote"], cancellation)).Output.Trim();
            var merge = (await RunAsync(repository.Root, ["config", "--get", $"branch.{repository.Branch}.merge"], cancellation)).Output.Trim();
            if (remote == "." || !merge.StartsWith("refs/heads/")) throw new InvalidOperationException("リモートの追跡先を設定してください。");
            return await CommandAsync(repository, ["push", "--", remote, $"HEAD:{merge}"], cancellation, network: true);
        }
        return await CommandAsync(repository, ["push", "--set-upstream", "--", repository.PushRemote!, $"HEAD:refs/heads/{repository.Branch}"], cancellation, network: true);
    }
    public async Task<string> SwitchAsync(GitSnapshot repository, GitBranch branch, CancellationToken cancellation = default)
    {
        if (!repository.Branches.Contains(branch)) throw new ArgumentException("ブランチ一覧を更新してください。");
        return await CommandAsync(repository, branch.Remote ? ["switch", "--track", "--", branch.Name] : ["switch", "--", branch.Name], cancellation);
    }
    public async Task<string> CreateBranchAsync(GitSnapshot repository, string name, CancellationToken cancellation = default)
    {
        await RunAsync(repository.Root, ["check-ref-format", "--branch", name], cancellation);
        return await CommandAsync(repository, ["switch", "-c", name, "--"], cancellation);
    }
    public Task<string> StageAsync(GitSnapshot repository, GitChange? change, CancellationToken cancellation = default) =>
        CommandAsync(repository, change == null ? ["--literal-pathspecs", "add", "-A", "--", "."] :
            change.OriginalPath == null ? ["--literal-pathspecs", "add", "-A", "--", change.Path] :
            ["--literal-pathspecs", "add", "-A", "--", change.Path, change.OriginalPath], cancellation);
    public Task<string> UnstageAsync(GitSnapshot repository, GitChange? change, CancellationToken cancellation = default)
    {
        var paths = change == null ? new[] { "." } : change.OriginalPath == null ? new[] { change.Path } : new[] { change.Path, change.OriginalPath };
        return CommandAsync(repository, repository.HasHead ? ["--literal-pathspecs", "reset", "-q", "HEAD", "--", .. paths] :
            ["--literal-pathspecs", "rm", "--cached", "-r", "--", .. paths], cancellation);
    }
    public Task<string> CommitAsync(GitSnapshot repository, string message, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("コミットメッセージを入力してください。");
        return CommandAsync(repository, ["commit", "-m", message], cancellation);
    }

    private static async Task<string> CommandAsync(GitSnapshot repository, string[] arguments, CancellationToken cancellation, bool network = false)
    {
        if (arguments[0] != "fetch") {
            var branch = await RunAsync(repository.Root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellation, allowFailure: true);
            var sameBranch = repository.Detached ? branch.Code != 0 : branch.Output.TrimEnd('\r', '\n') == repository.Branch;
            if (!sameBranch) throw new IOException("外部でブランチが変更されました。一覧の更新後に再実行してください。");
            if (repository.Detached) {
                var head = await RunAsync(repository.Root, ["rev-parse", "HEAD"], cancellation);
                if (head.Output.Trim() != repository.HeadId) throw new IOException("外部で HEAD が変更されました。一覧の更新後に再実行してください。");
            }
        }
        var result = await RunAsync(repository.Root, arguments, cancellation, network: network);
        return (result.Output + result.Error).Trim();
    }

    private sealed record Result(int Code, string Output, string Error);
    private static async Task<Result> RunAsync(string directory, string[] arguments, CancellationToken cancellation, bool allowFailure = false, bool network = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(network ? 120 : 30));
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var variable in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_COMMON_DIR" }) start.Environment.Remove(variable);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "Never";
        start.Environment["GIT_EDITOR"] = "true";
        start.Environment["GIT_MERGE_AUTOEDIT"] = "no";
        start.Environment["LC_ALL"] = "C.UTF-8";
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("core.quotepath=false");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception ex) { throw new IOException("Git を起動できません。Git for Windows をインストールし、PATH を確認してください。", ex); }
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            throw new IOException("Git 操作がタイムアウトしました。認証やネットワークを確認してください。");
        }
        var result = new Result(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        if (result.Code != 0 && !allowFailure) throw new IOException(string.IsNullOrWhiteSpace(result.Error) ? result.Output.Trim() : result.Error.Trim());
        return result;
    }
}
