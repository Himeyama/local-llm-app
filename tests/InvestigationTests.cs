using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using LocalLlm.Gui.Services;

static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Reject(Action action)
{
    try { action(); } catch (ArgumentException) { return; }
    throw new Exception("Expected scope rejection");
}
static Investigation NewRun() => new() { Target = "https://owned.example/app/", Goal = "公開情報を確認", ExcludedPaths = ["/app/logout"], RoundLimit = 3, RequestLimit = 6 };
static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value)) };
static string Chunk(object delta, string? finish = null) => "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta, finish_reason = finish } } }) + "\n\n";
static HttpResponseMessage PlanResponse(object plan, string? finish = "stop", bool complete = true) => new(HttpStatusCode.OK)
{
    Content = new StringContent(Chunk(new { reasoning_content = "対象を確認。" }) + Chunk(new { reasoning_content = "証拠から次の検査を考えます。" }) +
        Chunk(new { content = JsonSerializer.Serialize(plan) }, finish) + (complete ? "data: [DONE]\n\n" : ""), System.Text.Encoding.UTF8, "text/event-stream")
};
static InvestigationTask Check(string url, string value = "Content-Security-Policy") => new() { Url = url, Check = "missing_header", Value = value, Hypothesis = "防御ヘッダー欠落の候補" };

var scope = new InvestigationScope(NewRun());
scope.Validate("https://owned.example/app/api?x=1", "GET");
foreach (var url in new[] { "https://other.example/app/", "http://owned.example/app/", "https://owned.example:444/app/", "https://owned.example/application", "https://owned.example/APP/a", "https://owned.example/app/logout", "https://owned.example/app/logout/a", "https://owned.example/app/%6cogout", "https://owned.example/app/%252e%252e/admin", "https://owned.example/app/%2e%2e/admin", "https://user:pass@owned.example/app/", "file:///app/", "https://owned.example/app/#x" })
    Reject(() => scope.Validate(url, "GET"));
Reject(() => scope.Validate("https://owned.example/app/", "POST"));
var encodedExclusion = NewRun(); encodedExclusion.ExcludedPaths = ["/app/%6cogout"];
Reject(() => new InvestigationScope(encodedExclusion).Validate("https://owned.example/app/logout", "GET"));
Console.WriteLine("PASS scope: origin, path boundaries, encodings, exclusions, methods");

int plans = 0, requests = 0, active = 0, maximum = 0;
string firstId = "";
var probe = new HttpClient(new Handler(async (request, ct) =>
{
    Interlocked.Increment(ref requests);
    var concurrency = Interlocked.Increment(ref active);
    maximum = Math.Max(maximum, concurrency);
    try
    {
        await Task.Delay(30, ct);
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='/app/api'>API</a>") };
        response.Headers.Add("Set-Cookie", "secret-session=abc");
        return response;
    }
    finally { Interlocked.Decrement(ref active); }
}));
var model = new HttpClient(new Handler(async (request, ct) =>
{
    if (request.RequestUri!.AbsolutePath == "/props") return Json(new { model_alias = "local-test-model" });
    var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
    Assert(sent["model"]!.GetValue<string>() == "local-test-model", "Must discover model alias");
    Assert(sent["stream"]!.GetValue<bool>(), "Planner must stream so reasoning can display during generation");
    var history = JsonNode.Parse(sent["messages"]![1]!["content"]!.GetValue<string>())!;
    Assert(!history["messages"]!.AsArray().Any(m => m?["Reasoning"] != null), "Display reasoning must not enter planner history");
    plans++;
    List<InvestigationTask> tasks;
    if (plans == 1) tasks = [Check("https://owned.example/app/"), Check("https://owned.example/app/api")];
    else if (plans == 2)
    {
        Assert(history["evidence"]!.AsArray().Count == 2, "Replan must receive both worker results");
        firstId = history["tasks"]![0]!["Id"]!.GetValue<string>();
        tasks = [new() { Url = "https://owned.example/app/next", Hypothesis = "依存する追加検査", DependsOn = [firstId] },
            Check("https://outside.example/"), Check("https://owned.example/app/logout"), Check("https://owned.example/app/")];
    }
    else tasks = [];
    return PlanResponse(new { summary = "検査計画", tasks });
}));
var run = NewRun();
int saved = 0;
var runner = new InvestigationRunner(model, probe);
var liveReasoning = new List<string>();
await runner.RunAsync(run, () => saved++, CancellationToken.None, liveReasoning.Add);
Assert(liveReasoning.Any(r => r == "対象を確認。") && liveReasoning.Contains("対象を確認。証拠から次の検査を考えます。"), "Reasoning fragments must reach live display");
Assert(run.Messages.Count(m => m.Reasoning == "対象を確認。証拠から次の検査を考えます。") == 3, "Each plan must preserve its own reasoning");
Assert(requests == 3 && plans == 3 && maximum == 2, "Must replan and execute two parallel HTTP workers");
Assert(run.Tasks.Count(t => t.Status == "拒否") == 3, "Must reject out of scope, excluded, and duplicate tasks");
Assert(run.Findings.Count == 2 && run.Evidence.Count == 3, "Findings require actual evidence");
Assert(run.Edges.Any(e => e.From == firstId && e.Relation == "depends_on"), "Missing dependency graph");
Assert(run.Assets.Any(a => a.Kind == "domain") && run.Edges.Any(e => e.Relation == "supports_candidate"), "Missing asset/evidence/finding graph");
Assert(run.Evidence.All(e => e.Headers["Set-Cookie"] == "[redacted]"), "Cookies must be redacted");
Assert(saved > 3, "Must persist intermediate state");
Console.WriteLine("PASS planner/worker loop, concurrency, reuse, dependency/asset graph, evidence, scope rejection");

// A retest performs a fresh request without calling the model or reusing cached evidence.
int retests = 0;
var retestProbe = new HttpClient(new Handler((_, _) =>
{
    retests++;
    var response = new HttpResponseMessage(retests == 3 ? HttpStatusCode.NotFound : HttpStatusCode.OK) { Content = new StringContent("fixed page") };
    if (retests >= 2) response.Headers.Add("Content-Security-Policy", "default-src 'self'");
    return Task.FromResult(response);
}));
var retester = new InvestigationRunner(model, retestProbe);
var finding = run.Findings[0];
await retester.RetestAsync(run, finding, () => { }, CancellationToken.None);
Assert(finding.Status == "引き続き再現可能", "Expected reproduced finding");
await retester.RetestAsync(run, finding, () => { }, CancellationToken.None);
Assert(finding.Status == "修正済み", "Expected fixed predicate");
await retester.RetestAsync(run, finding, () => { }, CancellationToken.None);
Assert(finding.Status == "確認できません" && finding.Retests.Count == 3, "404 must be inconclusive and preserve retest history");
Assert(plans == 3 && run.Evidence.Count(e => e.Retest) == 3, "Retest must be independent of planner");
var marker = new InvestigationTask { Check = "body_contains", Value = "specific-marker" };
Assert(InvestigationRunner.Matches(marker, new() { StatusCode = 200, Truncated = true }) == null, "Truncated evidence cannot prove absence");
Assert(InvestigationRunner.Matches(marker, new() { StatusCode = 200, Error = "timeout" }) == null, "Failed request cannot prove absence");
Console.WriteLine("PASS independent retest: reproduced / fixed / inconclusive, partial/error evidence");

// Enforce budget even if planner asks for more; no request occurs for oversized plans.
var limited = NewRun(); limited.RequestLimit = 1;
plans = 0; requests = 0;
try { await runner.RunAsync(limited, () => { }, CancellationToken.None); throw new Exception("Oversized plan accepted"); }
catch (IOException) { }
Assert(requests == 0 && limited.Status == "失敗", "Oversized plans must not execute");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    var cancelled = NewRun();
    try { await runner.RunAsync(cancelled, () => { }, cancellation.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    Assert(cancelled.Status == "停止", "Cancelled run must retain stopped status");
}
Console.WriteLine("PASS request budget, cancellation");

foreach (var failure in new[] { (Finish: "stop", Complete: false), (Finish: "length", Complete: true) })
{
    var brokenModel = new HttpClient(new Handler((request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/props"
        ? Json(new { model_alias = "local-test-model" }) : PlanResponse(new { summary = "partial", tasks = new[] { Check("https://owned.example/app/") } }, failure.Finish, failure.Complete))));
    var broken = NewRun();
    int previousRequests = requests;
    try { await new InvestigationRunner(brokenModel, probe).RunAsync(broken, () => { }, CancellationToken.None); throw new Exception("Incomplete plan executed"); }
    catch (IOException) { }
    Assert(requests == previousRequests && broken.Tasks.Count == 0, "Incomplete/length-limited stream must never execute tests");
}
Console.WriteLine("PASS streamed reasoning, display-only history, disconnected/length-limited plan rejection");

var directory = Path.Combine(AppContext.BaseDirectory, "InvestigationTests-" + Guid.NewGuid().ToString("N"));
try
{
    var store = new InvestigationStore(directory);
    store.Save(run);
    var loaded = store.Load().Single();
    Assert(loaded.Edges.Count == run.Edges.Count && loaded.Findings[0].Retests.Count == 3, "Graphs/retest evidence did not round-trip");
    Assert(loaded.Messages.Any(m => m.Reasoning == "対象を確認。証拠から次の検査を考えます。"), "Reasoning did not round-trip with investigation history");
    run.Status = "実行中"; run.Tasks[0].Status = "実行中"; store.Save(run);
    loaded = store.Load().Single();
    Assert(loaded.Status.Contains("中断") && loaded.Tasks[0].Status == "中断", "Must recover interrupted runs");
    File.WriteAllText(Path.Combine(directory, "broken.json"), "{");
    Assert(store.Load().Count == 1, "Corrupt history must not hide good records");
}
finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
Console.WriteLine("PASS persistence and interrupted-run recovery");

// Exercise the production HTTP handler against loopback, never against a third-party site.
var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
listener.Start();
try
{
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(async () =>
    {
        using var connection = await listener.AcceptTcpClientAsync();
        using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, leaveOpen: true);
        while (await reader.ReadLineAsync() is { Length: > 0 }) { }
        var bytes = System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://127.0.0.1:{port}/outside\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes);
        listener.Stop();
    });
    using var realProbe = InvestigationRunner.CreateProbeClient();
    using var response = await realProbe.GetAsync($"http://127.0.0.1:{port}/allowed/");
    Assert(response.StatusCode == HttpStatusCode.Found, "Production handler followed a redirect");
    await server;
}
finally { listener.Stop(); }
Console.WriteLine("PASS real loopback HTTP: redirects are not followed");

sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> action) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => action(request, cancellationToken);
}
