using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LocalLlm.Gui.Services;

public static class CodexLanConnection
{
    public const int Port = 8087;
    public const string ProcessName = "Codex LAN proxy";
    public const string HealthUrl = "http://127.0.0.1:8087/health";
    public static string[] BaseUrls() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
        .Select(info => info.Address)
        .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address) && !address.ToString().StartsWith("169.254.", StringComparison.Ordinal))
        .Select(address => $"http://{address}:{Port}/v1").Distinct().ToArray();

    public static bool IsReady(string? health)
    {
        if (health == null) return false;
        try
        {
            using var document = JsonDocument.Parse(health);
            var root = document.RootElement;
            return root.GetProperty("status").GetString() == "ok"
                && root.GetProperty("visionPassthrough").GetBoolean()
                && root.GetProperty("toolCompatibility").GetInt32() == 1
                && root.GetProperty("upstream").GetString() == "http://127.0.0.1:9931"
                && root.GetProperty("listenHost").GetString() == "0.0.0.0"
                && root.GetProperty("listenPort").GetInt32() == Port;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    public static string BashCommand(string baseUrl, string model, int context)
    {
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
        static string Config(string key, string value) => " -c " + Quote(key + "=" + JsonSerializer.Serialize(value));
        return "export OPENAI_API_KEY=local; codex --sandbox danger-full-access --model " + Quote(model)
            + Config("model_provider", "lan") + Config("model_providers.lan.name", "llama-LAN")
            + Config("model_providers.lan.base_url", baseUrl) + Config("model_providers.lan.wire_api", "responses")
            + Config("model_providers.lan.env_key", "OPENAI_API_KEY")
            + " -c " + Quote("model_context_window=" + context.ToString(System.Globalization.CultureInfo.InvariantCulture))
            + Config("model_reasoning_effort", "xhigh");
    }
}
