using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Infrastructure;

/// <summary>
/// Validates model synchronization between primary and fallback Ollama routers on startup.
/// Fails startup if model lists don't match or required model is missing on either endpoint.
/// Includes 10-second timeout to prevent startup hang.
/// </summary>
public sealed class OllamaModelSyncValidator
{
    private readonly LlmGatewayOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaModelSyncValidator> _logger;

    public OllamaModelSyncValidator(
        IOptions<LlmGatewayOptions> options,
        HttpClient httpClient,
        ILogger<OllamaModelSyncValidator> logger)
    {
        _options = options.Value;
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> ValidateAsync(CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));  // 10-second timeout

        var primaryEndpoint = _options.Providers.OllamaRouter.PrimaryEndpoint;
        var fallbackEndpoint = _options.Providers.OllamaRouter.FallbackEndpoint;
        var requiredModel = _options.Providers.OllamaRouter.Model;

        _logger.LogInformation("🔍 Validating Ollama model sync between {Primary} and {Fallback} (10s timeout)", 
            primaryEndpoint, fallbackEndpoint);

        try
        {
            var primaryModels = await GetModelsAsync(primaryEndpoint, timeoutCts.Token);
            var fallbackModels = await GetModelsAsync(fallbackEndpoint, timeoutCts.Token);

            if (primaryModels == null)
            {
                _logger.LogError("❌ Failed to query models from primary endpoint {Endpoint}", primaryEndpoint);
                return false;
            }

            if (fallbackModels == null)
            {
                _logger.LogError("❌ Failed to query models from fallback endpoint {Endpoint}", fallbackEndpoint);
                return false;
            }

            // Validate required model exists on PRIMARY
            if (!primaryModels.Contains(requiredModel))
            {
                _logger.LogError("❌ Required model {Model} not found on PRIMARY endpoint {Endpoint}", 
                    requiredModel, primaryEndpoint);
                return false;
            }

            // Validate required model exists on FALLBACK
            if (!fallbackModels.Contains(requiredModel))
            {
                _logger.LogError("❌ Required model {Model} not found on FALLBACK endpoint {Endpoint}", 
                    requiredModel, fallbackEndpoint);
                return false;
            }

            var primaryNames = primaryModels.OrderBy(m => m).ToList();
            var fallbackNames = fallbackModels.OrderBy(m => m).ToList();

            if (!primaryNames.SequenceEqual(fallbackNames))
            {
                var missingOnFallback = primaryNames.Except(fallbackNames).ToList();
                var missingOnPrimary = fallbackNames.Except(primaryNames).ToList();

                _logger.LogError("❌ Model sync validation FAILED:");
                if (missingOnFallback.Any())
                    _logger.LogError("   Missing on fallback (.12): {Models}", string.Join(", ", missingOnFallback));
                if (missingOnPrimary.Any())
                    _logger.LogError("   Missing on primary (.53): {Models}", string.Join(", ", missingOnPrimary));

                return false;
            }

            _logger.LogInformation("✅ Model sync validation PASSED. Both endpoints have identical models: {Models}", 
                string.Join(", ", primaryNames));
            return true;
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("❌ Model sync validation timed out after 10 seconds");
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Model sync validation threw exception");
            return false;
        }
    }

    private async Task<List<string>?> GetModelsAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            var tagsUrl = $"{endpoint}/api/tags";
            var response = await _httpClient.GetAsync(tagsUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("⚠️ Failed to query {Endpoint}: {StatusCode}", tagsUrl, response.StatusCode);
                return null;
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var models = new List<string>();

            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameElement))
                    {
                        models.Add(nameElement.GetString() ?? "unknown");
                    }
                }
            }

            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Exception querying {Endpoint}", endpoint);
            return null;
        }
    }
}
