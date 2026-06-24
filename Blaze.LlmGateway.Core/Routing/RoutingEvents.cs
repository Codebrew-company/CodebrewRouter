namespace Blaze.LlmGateway.Core.Routing;

public readonly record struct RouterStartEvent(int MessageCount);

public readonly record struct RouterCleanEvent(int OriginalChars, int CleanedChars, long ElapsedMs);

public readonly record struct RouterResolveEvent(
    string TaskType,
    int TokenCount,
    int ProviderCount,
    string ProviderChain,
    long ElapsedMs);

public readonly record struct RouterContextBudgetEvent(
    int Attempt,
    string Key,
    string Model,
    int CurrentTokens,
    int InputBudget,
    int MaxContext);

public readonly record struct RouterTryEvent(
    int Attempt,
    int Total,
    string Key,
    string Model,
    string TaskType);

public readonly record struct RouterProbeEvent(
    int Attempt,
    string Key,
    string Model,
    long FirstChunkMs,
    bool Success);

public readonly record struct RouterSuccessEvent(
    int Attempt,
    string Key,
    string Model,
    string TaskType,
    string? FinishReason,
    int? InputTokens,
    int? OutputTokens,
    long ElapsedMs);

public readonly record struct RouterFailEvent(
    int Attempt,
    string Key,
    string Model,
    string Message);

public readonly record struct RouterCompactEvent(
    int Attempt,
    string Key,
    int BeforeTokens,
    int AfterTokens);

public readonly record struct RouterSkipEvent(
    int Attempt,
    string Key,
    string Model,
    int CurrentTokens,
    int Budget,
    string Reason);

public readonly record struct RouterExhaustedEvent(
    int TotalAttempted,
    string TaskType,
    string FallbackKey);

public readonly record struct RouterMidstreamFailEvent(
    string Key,
    string Model);

public readonly record struct RouterStreamCompleteEvent(
    int ChunkCount,
    string Key,
    string Model,
    string TaskType,
    long ElapsedMs);

// ── Verbose Route-Decision Events ──────────────────────────────────────────────

/// <summary>Emitted when auto-routing begins: shows model, task classification, and strategy.</summary>
public readonly record struct RouterSelectEvent(
    string Model,
    string Task,
    string Strategy);

/// <summary>Emitted when a specific deployment is selected from the catalog pool.</summary>
public readonly record struct RouterDeployEvent(
    string Selected,
    string Because,
    int Candidates);

/// <summary>Emitted per deployment when filtering by health/capability.</summary>
public readonly record struct RouterHealthEvent(
    string Deployment,
    string Status,
    string Reason);

/// <summary>Emitted when a provider fails and the router triggers fallback.</summary>
public readonly record struct RouterFallbackEvent(
    string From,
    string To,
    string Reason,
    int Attempt);

/// <summary>Emitted when fusion dispatch begins, listing participating models.</summary>
public readonly record struct RouterFusionEvent(
    string[] Models,
    string Judge);

/// <summary>Emitted when fusion selects a winner.</summary>
public readonly record struct RouterFusionResultEvent(
    string Chosen,
    string Reason);
