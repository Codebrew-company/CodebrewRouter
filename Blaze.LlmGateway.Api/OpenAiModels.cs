using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.LlmGateway.Api;

/// <summary>Standard error envelope returned for invalid requests.</summary>
public record ErrorResponse(
    [property: JsonPropertyName("error")]
    ErrorDetail Error);

/// <summary>Detailed error payload modeled after OpenAI-style failures.</summary>
public record ErrorDetail(
    [property: JsonPropertyName("message")]
    string Message,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("code")]
    string Code);

/// <summary>Documentation-only SSE chunk example for chat completion streaming responses.</summary>
public record ChatCompletionStreamChunk(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("object")]
    string Object,
    [property: JsonPropertyName("created")]
    long Created,
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("choices")]
    IList<Choice> Choices);

/// <summary>Documentation-only SSE chunk example for legacy text completion streaming responses.</summary>
public record TextCompletionStreamChunk(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("object")]
    string Object,
    [property: JsonPropertyName("created")]
    long Created,
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("choices")]
    IList<TextStreamChoice> Choices);

/// <summary>Legacy text completion chunk choice emitted during streaming.</summary>
public record TextStreamChoice(
    [property: JsonPropertyName("text")]
    string Text,
    [property: JsonPropertyName("index")]
    int Index,
    [property: JsonPropertyName("finish_reason")]
    string? FinishReason);

/// <summary>Chat completion request (OpenAI-compatible)</summary>
public record ChatCompletionRequest(
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("messages")]
    IList<ChatMessageDto> Messages,
    [property: JsonPropertyName("temperature")]
    float? Temperature = null,
    [property: JsonPropertyName("max_tokens")]
    int? MaxTokens = null,
    [property: JsonPropertyName("stream")]
    bool Stream = false,
    [property: JsonPropertyName("top_p")]
    float? TopP = null,
    [property: JsonPropertyName("frequency_penalty")]
    float? FrequencyPenalty = null,
    [property: JsonPropertyName("presence_penalty")]
    float? PresencePenalty = null,
    [property: JsonPropertyName("tools")]
    IList<Tool>? Tools = null,
    [property: JsonPropertyName("max_completion_tokens")]
    int? MaxCompletionTokens = null,
    [property: JsonPropertyName("stop")]
    JsonElement? Stop = null,
    [property: JsonPropertyName("tool_choice")]
    JsonElement? ToolChoice = null,
    [property: JsonPropertyName("response_format")]
    JsonElement? ResponseFormat = null,
    [property: JsonPropertyName("stream_options")]
    JsonElement? StreamOptions = null,
    [property: JsonPropertyName("parallel_tool_calls")]
    bool? ParallelToolCalls = null,
    [property: JsonPropertyName("reasoning_effort")]
    string? ReasoningEffort = null,
    [property: JsonPropertyName("metadata")]
    IDictionary<string, string>? Metadata = null,
    [property: JsonPropertyName("store")]
    bool? Store = null);

/// <summary>Chat message DTO for requests</summary>
[JsonConverter(typeof(ChatMessageContentConverter))]
public record ChatMessageDto(
    [property: JsonPropertyName("role")]
    string Role,
    [property: JsonPropertyName("content")]
    string Content,
    [property: JsonPropertyName("name")]
    string? Name = null,
    [property: JsonPropertyName("tool_call_id")]
    string? ToolCallId = null,
    [property: JsonPropertyName("tool_calls")]
    IList<ToolCallDto>? ToolCalls = null,
    [property: JsonIgnore]
    IList<ChatContentPart>? ContentParts = null);

/// <summary>OpenAI-compatible multimodal content part preserved from message content arrays.</summary>
public record ChatContentPart(
    string Type,
    string? Text = null,
    string? ImageUrl = null,
    string? Detail = null,
    string? MediaType = null,
    string? FileId = null,
    string? FileName = null);

/// <summary>Tool call emitted by an assistant message.</summary>
public record ToolCallDto(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("function")]
    ToolCallFunctionDto? Function = null,
    [property: JsonPropertyName("index")]
    int? Index = null);

/// <summary>Function payload for an OpenAI-style tool call.</summary>
public record ToolCallFunctionDto(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("arguments")]
    string Arguments);

/// <summary>Tool specification</summary>
public record Tool(
    [property: JsonPropertyName("type")]
    string Type,
    [property: JsonPropertyName("function")]
    FunctionDefinition Function);

/// <summary>Function definition for tools</summary>
public record FunctionDefinition(
    [property: JsonPropertyName("name")]
    string Name,
    [property: JsonPropertyName("description")]
    string? Description = null,
    [property: JsonPropertyName("parameters")]
    object? Parameters = null);

/// <summary>Text completion request (legacy)</summary>
public record TextCompletionRequest(
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("prompt")]
    string Prompt,
    [property: JsonPropertyName("max_tokens")]
    int? MaxTokens = null,
    [property: JsonPropertyName("temperature")]
    float? Temperature = null,
    [property: JsonPropertyName("top_p")]
    float? TopP = null,
    [property: JsonPropertyName("frequency_penalty")]
    float? FrequencyPenalty = null,
    [property: JsonPropertyName("presence_penalty")]
    float? PresencePenalty = null,
    [property: JsonPropertyName("stream")]
    bool Stream = false);

/// <summary>Chat completion response</summary>
public record ChatCompletionResponse(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("object")]
    string Object,
    [property: JsonPropertyName("created")]
    long Created,
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("choices")]
    IList<Choice> Choices,
    [property: JsonPropertyName("usage")]
    Usage? Usage = null);

/// <summary>Choice in chat completion response</summary>
public record Choice(
    [property: JsonPropertyName("index")]
    int Index,
    [property: JsonPropertyName("message")]
    ChatMessageDto? Message,
    [property: JsonPropertyName("delta")]
    ChoiceDelta? Delta,
    [property: JsonPropertyName("finish_reason")]
    string? FinishReason);

/// <summary>Delta content for streaming responses</summary>
public record ChoiceDelta(
    [property: JsonPropertyName("role")]
    string? Role = null,
    [property: JsonPropertyName("content")]
    string? Content = null);

/// <summary>Text completion response</summary>
public record TextCompletionResponse(
    [property: JsonPropertyName("id")]
    string Id,
    [property: JsonPropertyName("object")]
    string Object,
    [property: JsonPropertyName("created")]
    long Created,
    [property: JsonPropertyName("model")]
    string Model,
    [property: JsonPropertyName("choices")]
    IList<TextChoice> Choices,
    [property: JsonPropertyName("usage")]
    Usage? Usage = null);

/// <summary>Text choice in completion response</summary>
public record TextChoice(
    [property: JsonPropertyName("index")]
    int Index,
    [property: JsonPropertyName("text")]
    string Text,
    [property: JsonPropertyName("finish_reason")]
    string? FinishReason);

/// <summary>Token usage information</summary>
public record Usage(
    [property: JsonPropertyName("prompt_tokens")]
    int PromptTokens,
    [property: JsonPropertyName("completion_tokens")]
    int CompletionTokens,
    [property: JsonPropertyName("total_tokens")]
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
    string? Extends = null,
    bool Enabled = true,
    string? ErrorMessage = null,
    string? AgentMode = null,
    string? Workflow = null,
    string? ResponseContract = null,
    IList<string>? Capabilities = null,
    bool ToolSupport = false,
    bool VisionSupport = false,
    bool CloudRequired = false,
    int? ContextWindow = null,
    IList<string>? McpServers = null,
    IList<string>? Skills = null,
    VirtualModelMemoryInfo? Memory = null);

public record VirtualModelMemoryInfo(
    bool Enabled,
    string Scope,
    string? Provider = null,
    IList<string>? Collections = null);

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
    DateTimeOffset? LastCheckedUtc,
    bool? SupportsVision = null);

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
    string? Extends,
    bool Enabled,
    string? ErrorMessage,
    IList<CodebrewRouterBackingModel> BackingModels,
    IList<CodebrewRouterFallbackRule> FallbackRules,
    string? AgentMode = null,
    string? Workflow = null,
    string? ResponseContract = null,
    IList<string>? Capabilities = null,
    bool ToolSupport = false,
    bool VisionSupport = false,
    bool CloudRequired = false,
    int? ContextWindow = null,
    IList<string>? McpServers = null,
    IList<string>? Skills = null,
    VirtualModelMemoryInfo? Memory = null);

/// <summary>Concrete provider model that can back the CodebrewRouter virtual model.</summary>
public record CodebrewRouterBackingModel(
    string Id,
    string Object,
    string Provider,
    string? OwnedBy = null,
    string? Source = null,
    bool Enabled = true,
    string? ErrorMessage = null,
    bool? SupportsVision = null);

/// <summary>Ordered provider chain used by CodebrewRouter for a classified task type.</summary>
public record CodebrewRouterFallbackRule(
    string TaskType,
    IList<string> Providers);
