using System.Diagnostics;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// On-demand deep probe: sends "Hello, what model are you using?" through the real
/// resolution + routing pipeline for every enabled catalog model and reports each reply.
/// Healthy = every model answered; Degraded = some failed; Unhealthy = all failed.
///
/// Tagged "models-probe" and served ONLY at /health/models/probe — never part of /health,
/// because each run performs real completions (including paid providers). Results are
/// cached for <see cref="CacheTtl"/> so hammering the endpoint doesn't hammer providers.
/// Registered as a singleton so the cache survives across requests.
/// </summary>
public sealed class ModelProbeHealthCheck(
    IModelCatalog catalog,
    IModelSelectionResolver resolver,
    ILogger<ModelProbeHealthCheck> logger) : IHealthCheck
{
    private const string ProbePrompt = "Hello, what model are you using?";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PerModelTimeout = TimeSpan.FromSeconds(45);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private HealthCheckResult? _cached;
    private DateTimeOffset _cachedAt;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - _cachedAt < CacheTtl)
            {
                return cached;
            }

            var result = await ProbeAllModelsAsync(cancellationToken);
            _cached = result;
            _cachedAt = DateTimeOffset.UtcNow;
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HealthCheckResult> ProbeAllModelsAsync(CancellationToken cancellationToken)
    {
        var models = (await catalog.GetAvailableModelsAsync(cancellationToken))
            .Where(model => model.Enabled)
            .Select(model => model.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length == 0)
        {
            return HealthCheckResult.Unhealthy("No enabled models in the catalog to probe.");
        }

        var results = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        var okCount = 0;

        // Sequential on purpose: local inference serializes behind one lock anyway, and
        // parallel-probing every cloud model at once is a rate-limit magnet.
        foreach (var modelId in models)
        {
            var (ok, detail) = await ProbeModelAsync(modelId, cancellationToken);
            results[modelId] = detail;
            if (ok)
            {
                okCount++;
            }
        }

        var summary = $"{okCount}/{models.Length} models answered \"{ProbePrompt}\".";
        return okCount == models.Length
            ? HealthCheckResult.Healthy(summary, results)
            : okCount > 0
                ? HealthCheckResult.Degraded(summary, data: results)
                : HealthCheckResult.Unhealthy(summary, data: results);
    }

    private async Task<(bool Ok, string Detail)> ProbeModelAsync(string modelId, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PerModelTimeout);

            var client = await resolver.ResolveAsync(modelId, timeout.Token);
            if (client is null)
            {
                return (false, "FAIL: no chat client resolved for this model id.");
            }

            // Stream and aggregate — the same path /v1/chat/completions uses (gateway is
            // streaming-by-default). Non-streaming GetResponseAsync returns empty text from
            // OllamaSharp for thinking-mode models (observed live on gemma4:e4b), so a
            // non-streaming probe would false-flag models that users can chat with fine.
            // 256 tokens because thinking models spend the first tokens on non-visible reasoning.
            var text = new System.Text.StringBuilder();
            await foreach (var update in client.GetStreamingResponseAsync(
                [new ChatMessage(ChatRole.User, ProbePrompt)],
                new ChatOptions { MaxOutputTokens = 256, Temperature = 0f },
                timeout.Token))
            {
                text.Append(update.Text);
            }

            var reply = text.ToString().Trim();
            if (reply.Length == 0)
            {
                return (false, $"FAIL ({stopwatch.ElapsedMilliseconds}ms): empty response.");
            }

            // Models rarely know their own deployment name, so a name-match assertion would
            // flag healthy models. Return the reply so a human can eyeball self-identification.
            const int snippetLength = 160;
            var snippet = reply.Length <= snippetLength ? reply : reply[..snippetLength] + "…";
            return (true, $"ok ({stopwatch.ElapsedMilliseconds}ms): {snippet}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Model probe timed out for {ModelId} after {Elapsed}ms", modelId, stopwatch.ElapsedMilliseconds);
            return (false, $"FAIL ({stopwatch.ElapsedMilliseconds}ms): timed out after {PerModelTimeout.TotalSeconds:F0}s.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Model probe failed for {ModelId}", modelId);
            return (false, $"FAIL ({stopwatch.ElapsedMilliseconds}ms): {ex.GetBaseException().Message}");
        }
    }
}
