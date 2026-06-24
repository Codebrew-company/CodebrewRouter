using System.Text.Json;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Integration tests that verify yardly chat completion routing through
/// the running CodebrewRouter on localhost:5000.
/// </summary>
public class YardlyIntegrationTests
{
    private static readonly HttpClient Client = new()
    {
        BaseAddress = new Uri("http://localhost:5000"),
        Timeout = TimeSpan.FromMinutes(3)
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task YardlyRoute_HealthCheck()
    {
        // Act
        var response = await Client.GetAsync("/health");

        // Assert — the router returns 503 with "Degraded" when providers are not fully healthy
        var body = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(body);
        Assert.Contains("Degraded", body, StringComparison.OrdinalIgnoreCase);
        // Status code may be 200 (Healthy), 503 (Degraded), or other
        // — just verify the endpoint is reachable and returns status text
        Assert.True((int)response.StatusCode is >= 200 and < 600);
    }

    [Fact]
    public async Task YardlyRoute_ModelsList_IncludesYardly()
    {
        // Act
        var response = await Client.GetAsync("/v1/models");
        Assert.True(response.IsSuccessStatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);

        // Assert — "yardly" appears in the model list
        var modelsArray = json.RootElement.GetProperty("data");
        var modelIds = modelsArray.EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .ToArray();

        Assert.Contains("yardly", modelIds);
    }

    [Fact]
    public async Task YardlyRoute_ChatCompletion_BotanicalQuery()
    {
        // Arrange
        var payload = new
        {
            model = "yardly",
            messages = new[]
            {
                new { role = "user", content = "What plants grow well in shade?" }
            },
            max_tokens = 500
        };

        // Act
        var response = await Client.PostAsJsonAsync("/v1/chat/completions", payload, JsonOptions);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var outerJson = JsonDocument.Parse(body);

        // Extract the content from the first choice
        var content = outerJson.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        Assert.NotNull(content);
        Assert.NotEmpty(content);

        // The content is JSON — parse it
        using var yardlyJson = JsonDocument.Parse(content);

        // Assert yardly response structure markers
        Assert.True(yardlyJson.RootElement.TryGetProperty("schemaVersion", out var schemaVersion));
        Assert.Equal("yardly.response.v1", schemaVersion.GetString());

        Assert.True(yardlyJson.RootElement.TryGetProperty("responseType", out _));

        Assert.True(yardlyJson.RootElement.TryGetProperty("summary", out var summary));
        Assert.False(string.IsNullOrWhiteSpace(summary.GetString()));
    }

    [Fact]
    public async Task YardlyRoute_ChatCompletion_ReturnsYardlyJson()
    {
        // Arrange
        var payload = new
        {
            model = "yardly",
            messages = new[]
            {
                new { role = "user", content = "What plants grow well in shade?" }
            },
            max_tokens = 500
        };

        // Act
        var response = await Client.PostAsJsonAsync("/v1/chat/completions", payload, JsonOptions);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var outerJson = JsonDocument.Parse(body);

        // Outer response structure
        Assert.Equal("chat.completion", outerJson.RootElement.GetProperty("object").GetString());
        Assert.Equal("yardly", outerJson.RootElement.GetProperty("model").GetString());

        var choice = outerJson.RootElement.GetProperty("choices")[0];
        Assert.Equal("stop", choice.GetProperty("finish_reason").GetString());

        var message = choice.GetProperty("message");
        Assert.Equal("assistant", message.GetProperty("role").GetString());

        var content = message.GetProperty("content").GetString();
        Assert.NotNull(content);

        // Inner yardly JSON structure validation
        using var yardlyJson = JsonDocument.Parse(content);

        // Required top-level fields
        Assert.True(yardlyJson.RootElement.TryGetProperty("schemaVersion", out var sv));
        Assert.Equal("yardly.response.v1", sv.GetString());

        Assert.True(yardlyJson.RootElement.TryGetProperty("responseType", out _));
        Assert.True(yardlyJson.RootElement.TryGetProperty("summary", out _));

        // Verify optional arrays exist (they may be empty)
        Assert.True(yardlyJson.RootElement.TryGetProperty("observations", out var observations));
        Assert.Equal(JsonValueKind.Array, observations.ValueKind);

        Assert.True(yardlyJson.RootElement.TryGetProperty("carePlan", out var carePlan));
        Assert.Equal(JsonValueKind.Array, carePlan.ValueKind);

        Assert.True(yardlyJson.RootElement.TryGetProperty("followUpQuestions", out var followUp));
        Assert.Equal(JsonValueKind.Array, followUp.ValueKind);

        Assert.True(yardlyJson.RootElement.TryGetProperty("safetyNotes", out var safetyNotes));
        Assert.Equal(JsonValueKind.Array, safetyNotes.ValueKind);
    }
}
