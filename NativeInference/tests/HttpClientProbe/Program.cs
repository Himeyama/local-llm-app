using System.Net.Http.Json;

// Match the GUI transport: JsonContent does not precompute Content-Length.
// The padding exercises a streamed request body larger than socket buffers.
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var request = new HttpRequestMessage(HttpMethod.Post, args[0]) {
    Content = JsonContent.Create(new {
        messages = new[] { new { role = "user", content = "What is 2 + 2? Reply with just the number." } },
        stream = true,
        max_tokens = 8,
        reasoning_effort = "none",
        stream_options = new { include_usage = true },
        probe_padding = new string('x', 256 * 1024)
    })
};
using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
var body = await response.Content.ReadAsStringAsync();
if (!response.IsSuccessStatusCode) throw new IOException($"HTTP {(int)response.StatusCode}: {body}");
if (!body.Contains("[DONE]") || !body.Contains("\"content\":\"4\"")) throw new IOException("Incomplete or incorrect SSE response: " + body);
Console.WriteLine("PASS: GUI HttpClient / JsonContent / streamed request / SSE");
