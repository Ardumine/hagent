using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HCore.Packages.Agent;

/// <summary>
/// Minimal client for the Deepseek OpenAI-compatible Chat Completions API.
/// Endpoint: https://api.deepseek.com/chat/completions (OpenAI wire format).
/// </summary>
public sealed class DeepseekClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(2) };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _endpoint;

    public DeepseekClient(string apiKey, string model = "deepseek-chat",
        string baseUrl = "https://api.deepseek.com")
    {
        _apiKey = apiKey;
        _model = model;
        _endpoint = baseUrl.TrimEnd('/') + "/chat/completions";
    }

    /// <summary>
    /// Send the conversation + tool schema, return the assistant message node
    /// (contains "content" and/or "tool_calls").
    /// </summary>
    public JsonNode Chat(JsonArray messages, JsonElement tools)
    {
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = messages.DeepClone(),
            ["tools"] = JsonNode.Parse(tools.GetRawText()),
            ["tool_choice"] = "auto",
            ["temperature"] = 0.2,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = Http.Send(req);
        var payload = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Deepseek API {(int)resp.StatusCode}: {payload}");

        var root = JsonNode.Parse(payload)
            ?? throw new InvalidOperationException("Empty response from Deepseek API.");

        var message = root["choices"]?[0]?["message"]
            ?? throw new InvalidOperationException($"Malformed response: {payload}");

        return message.DeepClone();
    }
}
