using System.Text;
using System.Text.Json;
using Xunit.Abstractions;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Live integration tests — sends "tell me a dad joke" to every model the gateway exposes.
/// Requires the Aspire stack to be running before these tests execute.
///
/// How to run:
///   1. dotnet run --project Blaze.LlmGateway.AppHost   (keep running)
///   2. dotnet test --filter "DadJoke" --logger "console;verbosity=detailed"
///      OR set env var GATEWAY_SKIP_LIVE= (empty) to opt-in automatically.
///
/// The test fetches /v1/models to discover currently-available models, then
/// fires a streaming and a non-streaming request at each one and reports the
/// full joke (or the exact error) so you can see exactly which providers fail
/// and why.
/// </summary>
public class DadJokeIntegrationTests(ITestOutputHelper output)
{
    private const string LiveSkip =
        "Live test — start the Aspire stack first (dotnet run --project Blaze.LlmGateway.AppHost), " +
        "then remove [Skip] or set env GATEWAY_SKIP_LIVE= to an empty string.";

    private static readonly string BaseUrl =
        Environment.GetEnvironmentVariable("GATEWAY_BASE_URL") ?? "http://localhost:5022";

    // ── Auto-discover + test all available models ─────────────────────────────

    /// <summary>
    /// Discovers every enabled model from GET /v1/models and sends a streaming
    /// "tell me a dad joke" to each one. Collects ALL results before asserting so
    /// you see every pass/fail in a single run.
    /// </summary>
    [Fact(Skip = LiveSkip)]
    public async Task DadJoke_AllAvailableModels_Streaming_EachRespondsWithContent()
    {
        using var http = MakeClient();

        var models = await FetchAvailableModelIdsAsync(http);
        Assert.True(models.Count > 0, $"No enabled models returned by {BaseUrl}/v1/models. " +
            "Check /v1/models/diagnostics to see which providers are unavailable.");

        output.WriteLine($"Found {models.Count} model(s): {string.Join(", ", models)}");

        var failures = new List<string>();

        foreach (var modelId in models)
        {
            output.WriteLine($"\n── {modelId} (stream=true) ──");
            var result = await SendDadJokeAsync(http, modelId, stream: true);
            output.WriteLine(result.Summary);

            if (!result.Passed)
                failures.Add($"[{modelId}] {result.FailReason}");
        }

        if (failures.Count > 0)
            Assert.Fail("One or more models failed:\n" + string.Join("\n", failures));
    }

    // ── Per-provider hardcoded tests (stream=true) ────────────────────────────

    [Theory(Skip = LiveSkip)]
    [InlineData("gpt-5.4",        "AzureFoundry")]
    [InlineData("gpt-4o-mini",    "GithubModels")]
    [InlineData("gemma4:e4b",     "OllamaLocal")]
    [InlineData("local-model",    "LmStudio")]
    [InlineData("codebrewRouter", "CodebrewRouter")]
    public async Task DadJoke_Streaming_ModelRespondsWithContent(string modelId, string provider)
    {
        using var http = MakeClient();

        output.WriteLine($"[{provider}] POST {BaseUrl}/v1/chat/completions model={modelId} stream=true");

        var result = await SendDadJokeAsync(http, modelId, stream: true);
        output.WriteLine(result.Summary);

        Assert.True(result.Passed, result.FailReason ?? "Unknown failure");
    }

    // ── Per-provider hardcoded tests (stream=false) ───────────────────────────

    [Theory(Skip = LiveSkip)]
    [InlineData("gpt-5.4",        "AzureFoundry")]
    [InlineData("gpt-4o-mini",    "GithubModels")]
    [InlineData("codebrewRouter", "CodebrewRouter")]
    public async Task DadJoke_NonStreaming_ModelRespondsWithContent(string modelId, string provider)
    {
        using var http = MakeClient();

        output.WriteLine($"[{provider}] POST {BaseUrl}/v1/chat/completions model={modelId} stream=false");

        var result = await SendDadJokeAsync(http, modelId, stream: false);
        output.WriteLine(result.Summary);

        Assert.True(result.Passed, result.FailReason ?? "Unknown failure");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private HttpClient MakeClient() =>
        new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(90) };

    private async Task<List<string>> FetchAvailableModelIdsAsync(HttpClient http)
    {
        var response = await http.GetAsync("/v1/models");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);

        return json.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Where(m => !m.TryGetProperty("enabled", out var en) || en.GetBoolean())
            .Select(m => m.GetProperty("id").GetString() ?? "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToList();
    }

    private async Task<RequestResult> SendDadJokeAsync(HttpClient http, string modelId, bool stream)
    {
        var payload = new
        {
            model = modelId,
            messages = new[] { new { role = "user", content = "tell me a dad joke" } },
            stream
        };

        var content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("/v1/chat/completions", content);
        }
        catch (Exception ex)
        {
            return RequestResult.Fail(modelId, stream, $"HTTP request threw: {ex.Message}", elapsed: sw.Elapsed);
        }
        sw.Stop();

        var rawBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            return RequestResult.Fail(modelId, stream,
                $"HTTP {(int)response.StatusCode} — {TrimBody(rawBody)}",
                elapsed: sw.Elapsed);
        }

        var text = stream ? ExtractStreamingText(rawBody) : ExtractNonStreamingText(rawBody);

        if (string.IsNullOrWhiteSpace(text))
        {
            return RequestResult.Fail(modelId, stream,
                $"HTTP 200 but response text is empty. Raw body: {TrimBody(rawBody)}",
                elapsed: sw.Elapsed);
        }

        return RequestResult.Ok(modelId, stream, text, sw.Elapsed);
    }

    private static string ExtractStreamingText(string body)
    {
        var sb = new StringBuilder();
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line.Contains("[DONE]"))
                continue;

            try
            {
                var json = JsonDocument.Parse(line["data: ".Length..]);
                if (!json.RootElement.TryGetProperty("choices", out var choices))
                    continue;

                var choice = choices.EnumerateArray().FirstOrDefault();
                if (choice.ValueKind == JsonValueKind.Undefined)
                    continue;

                if (choice.TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var contentEl) &&
                    contentEl.ValueKind == JsonValueKind.String)
                {
                    sb.Append(contentEl.GetString());
                }
            }
            catch (JsonException)
            {
                // malformed chunk — skip
            }
        }
        return sb.ToString();
    }

    private static string ExtractNonStreamingText(string body)
    {
        try
        {
            var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("choices", out var choices))
                return string.Empty;

            var choice = choices.EnumerateArray().FirstOrDefault();
            if (choice.ValueKind == JsonValueKind.Undefined)
                return string.Empty;

            if (choice.TryGetProperty("message", out var message) &&
                message.TryGetProperty("content", out var contentEl))
                return contentEl.GetString() ?? string.Empty;
        }
        catch (JsonException) { }
        return string.Empty;
    }

    private static string TrimBody(string body) =>
        body.Length > 600 ? body[..600] + "…" : body;

    private sealed record RequestResult(
        string ModelId,
        bool Stream,
        bool Passed,
        string? Text,
        string? FailReason,
        TimeSpan Elapsed)
    {
        public string Summary => Passed
            ? $"  ✓ {ElapsedMs}ms — \"{Text}\""
            : $"  ✗ {ElapsedMs}ms — {FailReason}";

        private string ElapsedMs => Elapsed.TotalMilliseconds.ToString("F0");

        public static RequestResult Ok(string id, bool stream, string text, TimeSpan elapsed) =>
            new(id, stream, true, text, null, elapsed);

        public static RequestResult Fail(string id, bool stream, string reason, TimeSpan elapsed) =>
            new(id, stream, false, null, reason, elapsed);
    }
}
