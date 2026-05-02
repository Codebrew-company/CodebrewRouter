using Blaze.LlmGateway.Core.ModelCatalog;

namespace Blaze.LlmGateway.Api;

public sealed class ModelAvailabilityRegistry : IModelAvailabilityRegistry
{
    private readonly object _gate = new();
    private AvailableModel[] _models = [];
    private Dictionary<string, ProviderAvailabilitySnapshot> _providers = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AvailableModel> GetModels(bool includeUnavailable = false)
    {
        lock (_gate)
        {
            return includeUnavailable
                ? [.. _models]
                : [.. _models.Where(model => model.Enabled)];
        }
    }

    public AvailableModel? FindModel(string modelId, bool includeUnavailable = false)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        lock (_gate)
        {
            return _models.FirstOrDefault(model =>
                string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase) &&
                (includeUnavailable || model.Enabled));
        }
    }

    public bool IsProviderAvailable(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        lock (_gate)
        {
            return _providers.TryGetValue(provider, out var snapshot) && snapshot.Enabled;
        }
    }

    public string? GetProviderError(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return null;
        }

        lock (_gate)
        {
            return _providers.TryGetValue(provider, out var snapshot)
                ? snapshot.ErrorMessage
                : null;
        }
    }

    public IReadOnlyList<ProviderAvailabilitySnapshot> GetProviders()
    {
        lock (_gate)
        {
            return [.. _providers.Values.OrderBy(provider => provider.Provider, StringComparer.OrdinalIgnoreCase)];
        }
    }

    public void UpdateSnapshot(
        IEnumerable<AvailableModel> models,
        IEnumerable<ProviderAvailabilitySnapshot> providers)
    {
        // Perform expensive LINQ operations OUTSIDE the lock
        var deduplicatedModels = models
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(model => model.Enabled).First())
            .OrderBy(model => model.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deduplicatedProviders = providers
            .GroupBy(provider => provider.Provider, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(provider => provider.Enabled).First(),
                StringComparer.OrdinalIgnoreCase);

        // Acquire lock only to swap the arrays/dictionaries (very fast)
        lock (_gate)
        {
            _models = deduplicatedModels;
            _providers = deduplicatedProviders;
        }
    }
}

public sealed record ProviderAvailabilitySnapshot(
    string Provider,
    bool Enabled,
    string? ErrorMessage,
    DateTimeOffset LastCheckedUtc);
