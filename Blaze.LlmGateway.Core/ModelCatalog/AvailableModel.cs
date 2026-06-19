namespace Blaze.LlmGateway.Core.ModelCatalog;

public sealed record AvailableModel(
    string Id,
    string Provider,
    string OwnedBy,
    string Source,
    string? Endpoint = null,
    bool SupportsChat = true,
    bool Enabled = true,
    string? ErrorMessage = null,
    DateTimeOffset? LastCheckedUtc = null,
    bool? SupportsVision = null);
