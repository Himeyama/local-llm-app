using System.Diagnostics;
using LocalLlm.Gui.Services;

var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "git-fixture-" + Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(root);
var git = new GitRepository();
try {
    Check(await git.ReadAsync("") == null && await git.ReadAsync(root) == null, "Only existing work trees are enabled");
    var repo = Path.Combine(root, "repo 日本語"); Directory.CreateDirectory(repo);
    Check(await git.ReadAsync(repo) == null, "Workspace starts outside Git before git init");
    await Cmd(repo, "init", "-b", "main"); await Identity(repo);
    var state = await State(repo);
    Check(!state.HasHead && state.Branch == "main" && !state.CanPush && !state.CanPull, "Unborn branch state");
    const string original = "日本語 [test] ' spaced.txt";
    await File.WriteAllTextAsync(Path.Combine(repo, original), "first\n");
    await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "other\n");
    state = await State(repo);
    await git.StageAsync(state, state.Changes.Single(change => change.Path == original));
    state = await State(repo);
    Check(state.Changes.Single(change => change.Path == original).IsStaged && !state.Changes.Single(change => change.Path == "other.txt").IsStaged, "Literal path staging");
    await git.UnstageAsync(state, state.Changes.Single(change => change.Path == original));
    Check((await State(repo)).Changes.All(change => !change.IsStaged) && File.Exists(Path.Combine(repo, original)), "Unstage initial commit preserves files");
    await git.StageAsync(await State(repo), null);
    await git.CommitAsync(await State(repo), "initial\n\n日本語 message");
    var nested = Path.Combine(repo, "nested"); Directory.CreateDirectory(nested);
    state = await State(nested);
    Check(Path.GetFullPath(state.Root) == repo && state.HasHead && state.Changes.Count == 0, "Detect root from nested workspace");
    await git.CreateBranchAsync(state, "feature/test");
    state = await State(repo); Check(state.Branch == "feature/test", "Create and switch");
    await git.SwitchAsync(state, state.Branches.Single(branch => branch.Name == "main"));
    Check((await State(repo)).Branch == "main", "Switch existing branch");
    await Reject(() => git.CreateBranchAsync(state, "--bad"));
    await Reject(() => git.CreateBranchAsync(state, "invalid name"));
    await Cmd(repo, "mv", "--", original, "renamed 日本語.txt");
    state = await State(repo);
    var renamed = state.Changes.Single();
    Check(renamed.OriginalPath == original && renamed.Path == "renamed 日本語.txt" && renamed.IsStaged, "NUL-delimited rename parsing");
    await git.UnstageAsync(state, renamed);
    state = await State(repo); Check(state.Changes.Count == 2 && state.Changes.All(change => !change.IsStaged), "Unstage rename");
    await git.StageAsync(state, null); await git.CommitAsync(await State(repo), "rename");
    await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "staged\n");
    await git.StageAsync(await State(repo), null);
    await File.AppendAllTextAsync(Path.Combine(repo, "other.txt"), "unstaged\n");
    state = await State(repo); Check(state.Changes.Single().IsStaged && state.Changes.Single().IsUnstaged, "Partial staging appears in both groups");
    await git.StageAsync(state, null); await git.CommitAsync(await State(repo), "update");
    var remote = Path.Combine(root, "remote.git"); Directory.CreateDirectory(remote); await Cmd(remote, "init", "--bare", "-b", "main");
    Check(await git.ReadAsync(remote) == null, "Bare repository is not editable");
    await Cmd(repo, "remote", "add", "origin", remote);
    state = await State(repo); Check(state.CanPush && !state.CanPull && state.Upstream == null, "First push supported");
    await git.PushAsync(state);
    state = await State(repo); Check(state.Upstream == "origin/main" && state.Ahead == 0 && state.CanPull, "First push configures upstream");
    var clone = Path.Combine(root, "clone"); await Cmd(root, "clone", remote, clone); await Identity(clone);
    await File.WriteAllTextAsync(Path.Combine(clone, "remote.txt"), "remote\n");
    await git.StageAsync(await State(clone), null); await git.CommitAsync(await State(clone), "remote change"); await git.PushAsync(await State(clone));
    await git.FetchAsync(await State(repo));
    Check((await State(repo)).Behind == 1, "Incoming count after fetch");
    await git.PullAsync(await State(repo)); Check(File.Exists(Path.Combine(repo, "remote.txt")) && (await State(repo)).Behind == 0, "Fast-forward pull");
    await File.WriteAllTextAsync(Path.Combine(repo, "local.txt"), "local\n");
    await git.StageAsync(await State(repo), null); await git.CommitAsync(await State(repo), "local change");
    Check((await State(repo)).Ahead == 1, "Outgoing count");
    // push.default=matching must never push other branches.
    await Cmd(repo, "config", "push.default", "matching");
    await git.PushAsync(await State(repo)); Check((await State(repo)).Ahead == 0, "Explicit current branch push");
    await Cmd(clone, "switch", "-c", "remote-feature");
    await Cmd(clone, "push", "-u", "origin", "remote-feature");
    await git.FetchAsync(await State(repo)); state = await State(repo);
    await git.SwitchAsync(state, state.Branches.Single(branch => branch.Name == "origin/remote-feature" && branch.Remote));
    Check((await State(repo)).Branch == "remote-feature", "Remote tracking checkout");
    await Cmd(repo, "checkout", "--detach"); state = await State(repo);
    Check(state.Detached && !state.CanPush, "Detached HEAD");
    await git.CreateBranchAsync(state, "recovered"); Check((await State(repo)).Branch == "recovered", "Recover detached HEAD as branch");
    // Divergence must fail instead of creating an implicit merge.
    state = await State(repo); await git.SwitchAsync(state, state.Branches.Single(branch => branch.Name == "main"));
    await File.WriteAllTextAsync(Path.Combine(repo, "diverged.txt"), "local\n"); await git.StageAsync(await State(repo), null); await git.CommitAsync(await State(repo), "local divergence");
    await Cmd(clone, "switch", "main");
    await File.WriteAllTextAsync(Path.Combine(clone, "remote2.txt"), "remote\n"); await git.StageAsync(await State(clone), null); await git.CommitAsync(await State(clone), "remote divergence");
    await Cmd(clone, "pull", "--no-rebase", "--no-edit"); await git.PushAsync(await State(clone));
    // The remote merge includes the prior pushed commit but not the new local divergence.
    await git.FetchAsync(await State(repo)); state = await State(repo);
    Check(state.Ahead > 0 && state.Behind > 0, "Diverged counts"); await Reject(() => git.PullAsync(state));
    // A genuine unmerged entry remains visible until explicitly resolved and staged.
    await Cmd(repo, "branch", "conflict-base");
    await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "main side\n"); await git.StageAsync(await State(repo), null); await git.CommitAsync(await State(repo), "main side");
    await Cmd(repo, "switch", "conflict-base");
    await File.WriteAllTextAsync(Path.Combine(repo, "other.txt"), "other side\n"); await git.StageAsync(await State(repo), null); await git.CommitAsync(await State(repo), "other side");
    await Reject(() => Cmd(repo, "merge", "main"));
    state = await State(repo); Check(state.Changes.Any(change => change.Conflicted && change.IsUnstaged && !change.IsStaged), "Conflict parsing");
    Console.WriteLine("Git integration tests passed (local repositories only).");
}
finally {
    // Git makes some object files read-only; only remove this test's unique fixture directory.
    if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(root).StartsWith("git-fixture-")) throw new Exception("Unsafe fixture cleanup");
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
    Directory.Delete(root, true);
}
async Task<GitSnapshot> State(string directory) => await git.ReadAsync(directory) ?? throw new Exception("Repository missing: " + directory);
static void Check(bool value, string message) { if (!value) throw new Exception(message); }
static async Task Reject(Func<Task> action) { try { await action(); } catch (IOException) { return; } catch (ArgumentException) { return; } throw new Exception("Expected Git operation to fail"); }
static async Task Identity(string directory) { await Cmd(directory, "config", "user.name", "Git integration test"); await Cmd(directory, "config", "user.email", "test@example.invalid"); await Cmd(directory, "config", "commit.gpgsign", "false"); await Cmd(directory, "config", "core.autocrlf", "false"); }
static async Task Cmd(string directory, params string[] arguments)
{
    var start = new ProcessStartInfo("git") { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start)!;
    var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync(); var details = await output + await error;
    if (process.ExitCode != 0) throw new IOException(string.Join(' ', arguments) + ": " + details);
}
