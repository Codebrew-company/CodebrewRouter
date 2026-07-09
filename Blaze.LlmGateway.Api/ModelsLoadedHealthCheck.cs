using System.Net.Http.Headers;
using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Probes the actual /v1/models endpoint and fails when zero models are returned.
/// Unlike checking the in-memory registry (which is optimistically seeded),
/// this makes a real HTTP roundtrip through the full pipeline: auth, routing,
/// model catalog, and response formatting. If the endpoint can't serve any
/// models, the check returns Unhealthy and Aspire stops routing traffic.
/// </summary>
public sealed class ModelsLoadedHealthCheck(
    IHttpClientFactory httpClientFactory,
    IServer server,
    IOptionsMonitor<LlmGatewayOptions> options) : IHealthCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = ResolveBaseUrl();
        if (baseUrl is null)
        {
            return HealthCheckResult.Unhealthy("Could not determine the server's base URL for the models probe.");
        }

        var client = httpClientFactory.CreateClient("ModelsHealthCheck");
        client.Timeout = TimeSpan.FromSeconds(10);
        client.BaseAddress = baseUrl;

        // Use the health-check key if auth enforcement is on; otherwise no header needed.
        var auth = options.CurrentValue.Auth;
        if (!string.IsNullOrWhiteSpace(auth.HealthCheckKey))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", auth.HealthCheckKey);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await client.GetAsync("/v1/models", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Unhealthy(
                    $"Models endpoint returned HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(body);

            if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return HealthCheckResult.Unhealthy("Models endpoint response is missing the 'data' array.");
            }

            var count = data.GetArrayLength();
            if (count == 0)
            {
                return HealthCheckResult.Unhealthy(
                    "Models endpoint returned 0 models. No providers are serving yet.");
            }

            var byProvider = data.EnumerateArray()
                .Select(m => m.TryGetProperty("provider", out var p) ? p.GetString() ?? "?" : "?")
                .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            return HealthCheckResult.Healthy(
                $"{count} model(s) serving across {byProvider.Count} provider(s).",
                data: byProvider.ToDictionary<KeyValuePair<string, int>, string, object>(
                    kvp => kvp.Key, kvp => (object)kvp.Value, StringComparer.OrdinalIgnoreCase));
        }
        catch (TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("Models probe timed out — the endpoint may be hung.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy($"Models probe failed: {ex.Message}", ex);
        }
        finally
        {
            response?.Dispose();
        }
    }

    private Uri? ResolveBaseUrl()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses is null || addresses.Count == 0)
            return null;

        // Prefer HTTP (health checks don't need TLS).
        var http = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        return new Uri(http ?? addresses.First());
    }
}
