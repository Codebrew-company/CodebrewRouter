namespace Blaze.LlmGateway.Api;

/// <summary>Standard error envelope returned for invalid requests.</summary>
public record ErrorResponse(
    ErrorDetail Error);

/// <summary>Detailed error payload modeled after OpenAI-style failures.</summary>
public record ErrorDetail(
    string Message,
    string Type,
    string Code);

/// <summary>Documentation-only SSE chunk example for chat completion streaming responses.</summary>
public record ChatCompletionStreamChunk(
    string Id,
    string Object,
    long Created,
    string Model,
    IList<Choice> Choices);

/// <summary>Documentation-only SSE chunk example for legacy text completion streaming responses.</summary>
public record TextCompletionStreamChunk(
    string Id,
    string Object,
    long Created,
    string Model,
    IList<TextStreamChoice> Choices);

/// <summary>Legacy text completion chunk choice emitted during streaming.</summary>
public record TextStreamChoice(
    string Text,
    int Index,
    string? FinishReason);

/// <summary>Chat completion request (OpenAI-compatible)</summary>
public record ChatCompletionRequest(
    string Model,
    IList<ChatMessageDto> Messages,
    float? Temperature = null,
    int? MaxTokens = null,
    bool Stream = false,
    float? TopP = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    IList<Tool>? Tools = null);

/// <summary>Chat message DTO for requests</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(ChatMessageContentConverter))]
public record ChatMessageDto(
    string Role,
    string Content);

/// <summary>Tool specification</summary>
public record Tool(
    string Type,
    FunctionDefinition Function);

/// <summary>Function definition for tools</summary>
public record FunctionDefinition(
    string Name,
    string? Description = null,
    object? Parameters = null);

/// <summary>Text completion request (legacy)</summary>
public record TextCompletionRequest(
    string Model,
    string Prompt,
    int? MaxTokens = null,
    float? Temperature = null,
    float? TopP = null,
    float? FrequencyPenalty = null,
    float? PresencePenalty = null,
    bool Stream = false);

/// <summary>Chat completion response</summary>
public record ChatCompletionResponse(
    string Id,
    string Object,
    long Created,
    string Model,
    IList<Choice> Choices,
    Usage? Usage = null);

/// <summary>Choice in chat completion response</summary>
public record Choice(
    int Index,
    ChatMessageDto? Message,
    ChoiceDelta? Delta,
    string? FinishReason);

/// <summary>Delta content for streaming responses</summary>
public record ChoiceDelta(
    string? Role = null,
    string? Content = null);

/// <summary>Text completion response</summary>
public record TextCompletionResponse(
    string Id,
    string Object,
    long Created,
    string Model,
    IList<TextChoice> Choices,
    Usage? Usage = null);

/// <summary>Text choice in completion response</summary>
public record TextChoice(
    int Index,
    string Text,
    string? FinishReason);

/// <summary>Token usage information</summary>
public record Usage(
    int PromptTokens,
    int CompletionTokens,
    int TotalTokens);

/// <summary>Models list response</summary>
public record ModelsResponse(
    string Object,
    IList<ModelInfo> Data);

/// <summary>Model information</summary>
public record ModelInfo(
    string Id,
    string Object,
    string Provider,
    string? OwnedBy = null,
    string? Source = null,
    bool Enabled = true,
    string? ErrorMessage = null);

/// <summary>Full diagnostics for configured model/provider connectivity.</summary>
public record ModelDiagnosticsResponse(
    string Status,
    DateTimeOffset CheckedAt,
    IList<ModelDiagnosticsInfo> Models,
    IList<ProviderDiagnosticsInfo> Providers);

/// <summary>Connectivity diagnostics for a concrete model.</summary>
public record ModelDiagnosticsInfo(
    string Id,
    string Provider,
    string? OwnedBy,
    string? Source,
    string? Endpoint,
    bool Enabled,
    string? ErrorMessage,
    DateTimeOffset? LastCheckedUtc);

/// <summary>Connectivity diagnostics for a provider.</summary>
public record ProviderDiagnosticsInfo(
    string Provider,
    bool Enabled,
    string? ErrorMessage,
    DateTimeOffset LastCheckedUtc);

/// <summary>Detailed model information for the CodebrewRouter virtual model.</summary>
public record CodebrewRouterModelsResponse(
    string Id,
    string Object,
    string Provider,
    string OwnedBy,
    string Source,
    bool Enabled,
    string? ErrorMessage,
    IList<CodebrewRouterBackingModel> BackingModels,
    IList<CodebrewRouterFallbackRule> FallbackRules);

/// <summary>Concrete provider model that can back the CodebrewRouter virtual model.</summary>
public record CodebrewRouterBackingModel(
    string Id,
    string Object,
    string Provider,
    string? OwnedBy = null,
    string? Source = null);

/// <summary>Ordered provider chain used by CodebrewRouter for a classified task type.</summary>
public record CodebrewRouterFallbackRule(
    string TaskType,
    IList<string> Providers);
