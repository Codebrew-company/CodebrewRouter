using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.LlmGateway.Api;

// Anthropic Messages API wire format (P4.3). Only the fields the gateway consumes.

public sealed record AnthropicMessagesRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IList<AnthropicMessage> Messages,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("system")] JsonElement? System = null,
    [property: JsonPropertyName("temperature")] float? Temperature = null,
    [property: JsonPropertyName("top_p")] float? TopP = null,
    [property: JsonPropertyName("stop_sequences")] IList<string>? StopSequences = null,
    [property: JsonPropertyName("stream")] bool Stream = false,
    [property: JsonPropertyName("tools")] IList<AnthropicTool>? Tools = null,
    [property: JsonPropertyName("tool_choice")] AnthropicToolChoice? ToolChoice = null,
    [property: JsonPropertyName("metadata")] JsonElement? Metadata = null);

public sealed record AnthropicMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] JsonElement Content);

public sealed record AnthropicTool(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("input_schema")] JsonElement? InputSchema = null);

public sealed record AnthropicToolChoice(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("name")] string? Name = null);

public sealed record AnthropicUsage(
    [property: JsonPropertyName("input_tokens")] int InputTokens,
    [property: JsonPropertyName("output_tokens")] int OutputTokens);

public sealed record AnthropicContentBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("id")] string? Id = null,
    [property: JsonPropertyName("name")] string? Name = null,
    [property: JsonPropertyName("input")] JsonElement? Input = null);

public sealed record AnthropicMessagesResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("content")] IList<AnthropicContentBlock> Content,
    [property: JsonPropertyName("stop_reason")] string? StopReason,
    [property: JsonPropertyName("stop_sequence")] string? StopSequence,
    [property: JsonPropertyName("usage")] AnthropicUsage Usage);

public sealed record AnthropicCountTokensRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IList<AnthropicMessage> Messages,
    [property: JsonPropertyName("system")] JsonElement? System = null,
    [property: JsonPropertyName("tools")] IList<AnthropicTool>? Tools = null);

public sealed record AnthropicCountTokensResponse(
    [property: JsonPropertyName("input_tokens")] int InputTokens);

public sealed record AnthropicErrorResponse(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("error")] AnthropicErrorDetail Error);

public sealed record AnthropicErrorDetail(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message);
