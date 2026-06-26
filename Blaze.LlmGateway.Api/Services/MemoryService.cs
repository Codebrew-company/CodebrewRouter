using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Blaze.LlmGateway.Api.Models;

namespace Blaze.LlmGateway.Api.Services;

/// <summary>
/// Service wrapping mem0 platform REST API for memory operations.
/// Uses HttpClient to call https://api.mem0.ai/v1/ with token auth.
/// user_id: "codebrew", agent_id: "brew"
/// </summary>
public class MemoryService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly JsonSerializerOptions _jsonOptions;

    private const string UserId = "codebrew";
    private const string AgentId = "brew";
    private const string BaseUrl = "https://api.mem0.ai/v1/";

    /// Reads MEM0_API_KEY from ~/.hermes/.env as fallback.
    private static string? ReadEnvKey()
    {
        var envPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".hermes", ".env");
        if (!File.Exists(envPath)) return null;
        var prefix = "MEM0_API_KEY";
        foreach (var line in File.ReadLines(envPath))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                var eq = line.IndexOf('=');
                if (eq >= 0) return line[(eq + 1)..].Trim().Trim('"').Trim('\'');
            }
        }
        return null;
    }

    public MemoryService(HttpClient http, IConfiguration configuration)
    {
        _http = http;
        _apiKey = configuration["MEM0_API_KEY"] 
            ?? ReadEnvKey()
            ?? throw new InvalidOperationException("MEM0_API_KEY not configured");

        _http.BaseAddress = new Uri(BaseUrl);
        _http.DefaultRequestHeaders.Authorization = 
            new AuthenticationHeaderValue("Token", _apiKey);

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Add a memory to mem0 for the codebrew/brew user-agent.
    /// </summary>
    public async Task<MemoryResult> AddMemory(
        string content, 
        Dictionary<string, string>? metadata = null)
    {
        var request = new AddMemoryRequest
        {
            UserId = UserId,
            AgentId = AgentId,
            Messages = new List<MemoryMessage>
            {
                new() { Role = "user", Content = content }
            },
            Metadata = metadata
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("memories/", httpContent);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        
        // API returns an array of results
        var results = JsonSerializer.Deserialize<List<MemoryResult>>(body, _jsonOptions);
        return results?.FirstOrDefault() 
            ?? throw new InvalidOperationException("Empty response from mem0 add memory");
    }

    /// <summary>
    /// Search memories by semantic query.
    /// </summary>
    public async Task<List<MemoryItem>> SearchMemory(string query, int topK = 5)
    {
        var request = new SearchMemoryRequest
        {
            UserId = UserId,
            AgentId = AgentId,
            Query = query,
            TopK = topK
        };

        var json = JsonSerializer.Serialize(request, _jsonOptions);
        var httpContent = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync("memories/search/", httpContent);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        
        // API returns a flat array of memory items
        var results = JsonSerializer.Deserialize<List<MemoryItem>>(body, _jsonOptions);
        return results ?? new List<MemoryItem>();
    }

    /// <summary>
    /// Get all memories with pagination.
    /// </summary>
    public async Task<MemoryGetAllResponse> GetAllMemories(int page = 1, int pageSize = 20)
    {
        var url = $"memories/?user_id={UserId}&agent_id={AgentId}&page={page}&page_size={pageSize}";
        var response = await _http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<MemoryGetAllResponse>(body, _jsonOptions);
        return result ?? new MemoryGetAllResponse();
    }
}
