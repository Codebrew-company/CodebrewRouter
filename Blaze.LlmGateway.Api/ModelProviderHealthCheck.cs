using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Blaze.LlmGateway.Api;

public sealed class ModelProviderHealthCheck(ModelAvailabilityRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var providers = registry.GetProviders()
            .Where(provider => !string.Equals(provider.Provider, "CodebrewRouter", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (providers.Length == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("No provider availability snapshot has been recorded yet."));
        }

        var unavailable = providers.Where(provider => !provider.Enabled).ToArray();
        if (unavailable.Length == providers.Length)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "All configured model providers are unavailable.",
                data: ToHealthData(providers)));
        }

        if (unavailable.Length > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"{unavailable.Length} of {providers.Length} configured model providers are unavailable.",
                data: ToHealthData(providers)));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "All configured model providers are available.",
            data: ToHealthData(providers)));
    }

    private static IReadOnlyDictionary<string, object> ToHealthData(IEnumerable<ProviderAvailabilitySnapshot> providers)
        => providers.ToDictionary(
            provider => provider.Provider,
            provider => (object)new
            {
                provider.Enabled,
                provider.ErrorMessage,
                provider.LastCheckedUtc
            },
            StringComparer.OrdinalIgnoreCase);
}
