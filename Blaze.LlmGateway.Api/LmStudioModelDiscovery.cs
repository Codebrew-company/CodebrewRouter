using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Blaze.LlmGateway.Core.ModelCatalog;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Queries LM Studio to discover available models via the /v1/models endpoint (OpenAI-compatible).
/// </summary>
public sealed class LmStudioModelDiscovery(
    HttpClient httpClient,
    ILogger<LmStudioModelDiscovery> logger)
{
    /// <summary>
    /// Query LM Studio for available models.
    /// </summary>
    public async Task<IReadOnlyList<AvailableModel>> DiscoverModelsAsync(
        string endpoint,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
        => (await TryDiscoverModelsAsync(endpoint, apiKey, cancellationToken)).Models;

    public async Task<ModelDiscoveryResult> TryDiscoverModelsAsync(
        string endpoint,
        string? apiKey = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Normalize endpoint: LM Studio endpoints like "http://host:port/v1" should become ".../v1/models"
            // Use proper path joining to preserve the /v1 segment
            var normalizedBase = endpoint.TrimEnd('/');
            var modelsUrl = normalizedBase.EndsWith("/models") ? normalizedBase : $"{normalizedBase}/models";
            
            logger.LogDebug("Querying LM Studio for available models at {Endpoint}", modelsUrl);

            using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);

            // Add API key if provided (LM Studio usually accepts any non-empty key)
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.Add("Authorization", $"Bearer {apiKey}");
            }

            var response = await httpClient.SendAsync(request, cancellationToken);
            
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("LM Studio /models endpoint returned {StatusCode}: {ReasonPhrase}", 
                    response.StatusCode, response.ReasonPhrase);
                response.EnsureSuccessStatusCode();
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogDebug("LM Studio response body: {ResponseBody}", content.Substring(0, Math.Min(200, content.Length)));
            
            try
            {
                var modelsData = System.Text.Json.JsonSerializer.Deserialize<ModelsListResponse>(content);
                logger.LogDebug("Deserialized modelsData: {ModelsData}", modelsData != null ? "not null" : "null");
                logger.LogDebug("Deserialized modelsData.Data: {DataCount}", modelsData?.Data?.Count ?? 0);

                if (modelsData?.Data == null)
                {
                    logger.LogWarning("LM Studio returned empty models list");
                    return new ModelDiscoveryResult(true, []);
                }

                var models = modelsData.Data
                    .Where(m => !string.IsNullOrWhiteSpace(m.Id))
                    .Select(m => new AvailableModel(
                        Id: m.Id!,
                        Provider: "LmStudio",
                        OwnedBy: m.OwnedBy ?? "lmstudio",
                        Source: "discovered",
                        Endpoint: endpoint))
                    .ToList();

                logger.LogInformation("Discovered {Count} models from LM Studio", models.Count);
                return new ModelDiscoveryResult(true, models);
            }
            catch (System.Text.Json.JsonException jex)
            {
                logger.LogError(jex, "Failed to deserialize LM Studio models response: {Content}", content.Substring(0, Math.Min(300, content.Length)));
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to discover models from LM Studio at {Endpoint}", endpoint);
            return new ModelDiscoveryResult(false, []);
        }
    }

    public sealed record ModelDiscoveryResult(
        bool Success,
        IReadOnlyList<AvailableModel> Models);

    /// <summary>Internal DTO for LM Studio model list response (OpenAI-compatible format)</summary>
    private sealed record ModelsListResponse(
        [property: JsonPropertyName("object")]
        string? Object,
        [property: JsonPropertyName("data")]
        List<ModelData>? Data);

    /// <summary>Internal DTO for a single model entry</summary>
    private sealed record ModelData(
        [property: JsonPropertyName("id")]
        string? Id,
        [property: JsonPropertyName("object")]
        string? Object,
        [property: JsonPropertyName("created")]
        long? Created,
        [property: JsonPropertyName("owned_by")]
        string? OwnedBy);
}
