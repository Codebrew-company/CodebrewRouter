using System.Text.Json;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api;

internal static class OpenAiProtocolMapper
{
    private const string NaturalLanguageContractPrompt =
        "Return a normal conversational Markdown response. Do not return a raw JSON object unless the user explicitly requested JSON.";

    private const string YardlyJsonContractPrompt = """
        Return only one valid JSON object. Do not wrap it in Markdown fences and do not add prose before or after it.
        The object must match the Yardly response contract:
        {
          "schemaVersion": "yardly.response.v1",
          "responseType": "plant_identification | plant_health_assessment | care_guidance | clarification | general_yardly",
          "summary": "short user-facing summary",
          "confidence": 0.0,
          "observations": [
            { "label": "what you observed", "evidence": "visible or user-provided evidence", "confidence": 0.0 }
          ],
          "possibleIssues": [
            { "name": "issue name", "likelihood": 0.0, "severity": "low | medium | high | unknown", "rationale": "why it may apply" }
          ],
          "carePlan": [
            { "title": "step title", "instructions": "practical instructions", "priority": "low | medium | high", "timeframe": "when to do it" }
          ],
          "followUpQuestions": ["question to ask when evidence is limited"],
          "safetyNotes": ["toxicity, edible safety, pesticide, or local expert caveats"],
          "ui": {
            "cards": [],
            "actions": [
              { "id": "ask_for_photo", "label": "Add a clearer photo", "type": "secondary" }
            ]
          }
        }
        Use empty arrays when a section does not apply. Use confidence values from 0.0 to 1.0.
        """;

    public static List<ChatMessage> ToChatMessages(JsonElement input)
    {
        if (input.ValueKind is JsonValueKind.String)
        {
            return [new ChatMessage(ChatRole.User, input.GetString() ?? string.Empty)];
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return [new ChatMessage(ChatRole.User, input.GetRawText())];
        }

        var messages = new List<ChatMessage>();
        foreach (var item in input.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                messages.Add(new ChatMessage(ChatRole.User, item.GetString() ?? string.Empty));
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var role = item.TryGetProperty("role", out var roleElement)
                ? roleElement.GetString()
                : "user";

            if (!item.TryGetProperty("content", out var contentElement))
            {
                messages.Add(new ChatMessage(ToChatRole(role), item.GetRawText()));
                continue;
            }

            var contents = ParseContentParts(contentElement);
            messages.Add(new ChatMessage(ToChatRole(role), contents));
        }

        return messages.Count == 0
            ? [new ChatMessage(ChatRole.User, input.GetRawText())]
            : messages;
    }

    private static List<AIContent> ParseContentParts(JsonElement element)
    {
        var contents = new List<AIContent>();

        if (element.ValueKind == JsonValueKind.String)
        {
            contents.Add(new TextContent(element.GetString() ?? string.Empty));
            return contents;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            contents.Add(new TextContent(element.GetRawText()));
            return contents;
        }

        foreach (var part in element.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.String)
            {
                contents.Add(new TextContent(part.GetString() ?? string.Empty));
                continue;
            }

            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = part.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString()
                : null;

            switch (type)
            {
                case "text":
                    if (part.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
                    {
                        contents.Add(new TextContent(textValue.GetString() ?? string.Empty));
                    }
                    break;

                case "image_url":
                    if (part.TryGetProperty("image_url", out var imageUrlElement))
                    {
                        var url = imageUrlElement.ValueKind == JsonValueKind.Object
                            ? imageUrlElement.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null
                            : imageUrlElement.GetString();

                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var mediaType = part.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
                            contents.Add(ToImageContent(url, mediaType));
                        }
                    }
                    break;

                case "input_image":
                    if (part.TryGetProperty("image_url", out var inputImageUrl))
                    {
                        var url = inputImageUrl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var mediaType = part.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
                            contents.Add(ToImageContent(url, mediaType));
                        }
                    }
                    break;

                case "video_url":
                    if (part.TryGetProperty("video_url", out var videoUrlElement))
                    {
                        var url = videoUrlElement.ValueKind == JsonValueKind.Object
                            ? videoUrlElement.TryGetProperty("url", out var urlProp) ? urlProp.GetString() : null
                            : videoUrlElement.GetString();

                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var mediaType = part.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
                            contents.Add(ToVideoContent(url, mediaType));
                        }
                    }
                    break;

                case "input_video":
                    if (part.TryGetProperty("video_url", out var inputVideoUrl))
                    {
                        var url = inputVideoUrl.GetString();
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            var mediaType = part.TryGetProperty("media_type", out var mt) ? mt.GetString() : null;
                            contents.Add(ToVideoContent(url, mediaType));
                        }
                    }
                    break;
            }
        }

        return contents;
    }

    private static AIContent ToImageContent(string imageUrl, string? mediaType)
    {
        var resolvedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? InferMediaType(imageUrl)
            : mediaType;

        return imageUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? new DataContent(new Uri(imageUrl), resolvedMediaType)
            : new UriContent(new Uri(imageUrl), resolvedMediaType);
    }

    private static AIContent ToVideoContent(string videoUrl, string? mediaType)
    {
        var resolvedMediaType = string.IsNullOrWhiteSpace(mediaType)
            ? InferMediaType(videoUrl)
            : mediaType;

        return videoUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? new DataContent(new Uri(videoUrl), resolvedMediaType)
            : new UriContent(new Uri(videoUrl), resolvedMediaType);
    }

    private static string InferMediaType(string uri)
    {
        if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var separator = uri.IndexOf(';');
            return separator > "data:".Length
                ? uri["data:".Length..separator]
                : "application/octet-stream";
        }

        var withoutQuery = uri.Split('?', '#')[0];
        return Path.GetExtension(withoutQuery).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".avi" => "video/x-msvideo",
            ".mkv" => "video/x-matroska",
            _ => "image/*"
        };
    }

    public static List<ChatMessage> ApplyInstructions(
        IList<ChatMessage> messages,
        string? instructions,
        string model,
        IOptions<LlmGatewayOptions> gatewayOptions)
    {
        var profile = gatewayOptions.Value.FindVirtualModel(model);
        var systemPrompts = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(profile?.SystemPrompt))
        {
            systemPrompts.Add(new ChatMessage(ChatRole.System, profile.SystemPrompt));
        }

        if (!string.IsNullOrWhiteSpace(instructions))
        {
            systemPrompts.Add(new ChatMessage(ChatRole.System, instructions));
        }

        if (profile is not null)
        {
            systemPrompts.Add(new ChatMessage(ChatRole.System, GetResponseContractPrompt(profile)));
        }

        return systemPrompts.Count == 0
            ? [.. messages]
            : [.. systemPrompts, .. messages];
    }

    public static async Task<IChatClient> ResolveClientAsync(
        string model,
        IChatClient chatClient,
        IModelSelectionResolver modelSelectionResolver,
        IModelAvailabilityRegistry availabilityRegistry,
        CancellationToken cancellationToken)
    {
        var selected = await modelSelectionResolver.ResolveAsync(model, cancellationToken);
        if (selected is not null)
        {
            return selected;
        }

        var configuredModel = availabilityRegistry.FindModel(model, includeUnavailable: true);
        if (configuredModel is { Enabled: false })
        {
            throw new InvalidOperationException(configuredModel.ErrorMessage ?? $"Model '{model}' is unavailable.");
        }

        return chatClient;
    }

    public static ChatOptions ToChatOptions(CreateResponseRequest request, IOptions<LlmGatewayOptions> gatewayOptions)
    {
        var options = new ChatOptions
        {
            ModelId = request.Model,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxOutputTokens ?? request.MaxCompletionTokens,
            AllowMultipleToolCalls = request.ParallelToolCalls,
            ResponseFormat = GetResponseFormatForModel(request.Model, gatewayOptions)
        };

        return options;
    }

    public static ChatResponseFormat? GetResponseFormatForModel(
        string model,
        IOptions<LlmGatewayOptions> gatewayOptions,
        ChatResponseFormat? requestedFormat = null)
    {
        var profile = gatewayOptions.Value.FindVirtualModel(model);
        return VirtualModelResponseContracts.RequiresYardlyJson(profile)
            ? ChatResponseFormat.Json
            : requestedFormat;
    }

    public static string NormalizeAssistantContent(
        string model,
        string content,
        IOptions<LlmGatewayOptions> gatewayOptions)
    {
        var profile = gatewayOptions.Value.FindVirtualModel(model);
        if (!VirtualModelResponseContracts.RequiresYardlyJson(profile))
        {
            return content;
        }

        var trimmed = StripJsonCodeFence(content);
        if (IsYardlyJsonObject(trimmed))
        {
            return trimmed;
        }

        return CreateYardlyFallbackJson(content);
    }

    public static ResponseObject ToResponseObject(
        CreateResponseRequest request,
        ChatResponse completion,
        IOptions<LlmGatewayOptions> gatewayOptions,
        string? conversationId = null,
        string? responseId = null)
    {
        var output = new List<ResponseOutputItem>();
        var outputText = NormalizeAssistantContent(request.Model, completion.Text ?? string.Empty, gatewayOptions);

        if (VirtualModelResponseContracts.RequiresYardlyJson(gatewayOptions.Value.FindVirtualModel(request.Model)))
        {
            output.Add(new ResponseOutputItem(
                Id: Ids.New("msg"),
                Type: "message",
                Status: "completed",
                Role: "assistant",
                Content: [new ResponseContentPart("output_text", outputText)]));
        }

        foreach (var message in output.Count == 0 ? completion.Messages ?? [] : [])
        {
            var mediaParts = message.Contents
                .OfType<DataContent>()
                .Select(dc => new ResponseContentPart(
                    Type: dc.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? "output_video" : "output_image",
                    ImageUrl: dc.Uri?.ToString()))
                .Concat(message.Contents
                    .OfType<UriContent>()
                    .Select(uc => new ResponseContentPart(
                        Type: uc.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true ? "output_video" : "output_image",
                        ImageUrl: uc.Uri?.ToString())))
                .ToList();

            if (!string.IsNullOrEmpty(message.Text) || mediaParts.Count > 0)
            {
                var contentParts = new List<ResponseContentPart>();
                if (!string.IsNullOrEmpty(message.Text))
                {
                    contentParts.Add(new ResponseContentPart(
                        Type: "output_text",
                        Text: message.Text));
                }
                contentParts.AddRange(mediaParts);

                output.Add(new ResponseOutputItem(
                    Id: Ids.New("msg"),
                    Type: "message",
                    Status: "completed",
                    Role: ResolveOpenAiRole(message.Role),
                    Content: contentParts));
            }

            foreach (var toolCall in message.Contents.OfType<FunctionCallContent>())
            {
                output.Add(new ResponseOutputItem(
                    Id: Ids.New("fc"),
                    Type: "function_call",
                    Status: "completed",
                    CallId: string.IsNullOrWhiteSpace(toolCall.CallId) ? Ids.New("call") : toolCall.CallId,
                    Name: toolCall.Name,
                    Arguments: JsonSerializer.Serialize(toolCall.Arguments)));
            }

            foreach (var toolResult in message.Contents.OfType<FunctionResultContent>())
            {
                output.Add(new ResponseOutputItem(
                    Id: Ids.New("fco"),
                    Type: "function_call_output",
                    Status: "completed",
                    CallId: toolResult.CallId,
                    Output: toolResult.Result?.ToString()));
            }
        }

        if (output.Count == 0)
        {
            output.Add(new ResponseOutputItem(
                Id: Ids.New("msg"),
                Type: "message",
                Status: "completed",
                Role: "assistant",
                Content: [new ResponseContentPart("output_text", outputText)]));
        }

        var usage = completion.Usage is null
            ? null
            : new Usage(
                PromptTokens: ToInt(completion.Usage.InputTokenCount),
                CompletionTokens: ToInt(completion.Usage.OutputTokenCount),
                TotalTokens: ToInt(
                    completion.Usage.TotalTokenCount ??
                    (completion.Usage.InputTokenCount.GetValueOrDefault() + completion.Usage.OutputTokenCount.GetValueOrDefault())));

        return new ResponseObject(
            Id: responseId ?? Ids.New("resp"),
            Object: "response",
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status: "completed",
            Model: request.Model,
            Output: output,
            OutputText: outputText,
            ConversationId: conversationId,
            PreviousResponseId: request.PreviousResponseId,
            Metadata: request.Metadata,
            Usage: usage);
    }

    public static string ExtractText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>();
            foreach (var part in element.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.String)
                {
                    parts.Add(part.GetString() ?? string.Empty);
                    continue;
                }

                if (part.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }

            return string.Join("\n", parts.Where(static text => !string.IsNullOrWhiteSpace(text)));
        }

        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        return element.GetRawText();
    }

    public static ConversationItem ToConversationItem(ChatMessage message)
    {
        var text = message.Text;
        var imageParts = message.Contents
            .OfType<DataContent>()
            .Select(dc => (uri: dc.Uri?.ToString(), isVideo: dc.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true))
            .Concat(message.Contents
                .OfType<UriContent>()
                .Select(uc => (uri: uc.Uri?.ToString(), isVideo: uc.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.uri))
            .ToList();

        var content = imageParts.Count > 0
            ? string.Join("\n", new[] { text ?? string.Empty }
                .Concat(imageParts.Select(pair => pair.isVideo ? $"![video]({pair.uri})" : $"![image]({pair.uri})"))
                .Where(static s => !string.IsNullOrWhiteSpace(s)))
            : text;

        return new(
            Type: "message",
            Role: ResolveOpenAiRole(message.Role),
            Content: content,
            Id: Ids.New("msg"),
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    public static ConversationItem ToConversationItem(ResponseOutputItem item)
    {
        if (item.Content is { Count: > 0 })
        {
            var textParts = item.Content
                .Select(content => content.Text)
                .Where(static text => !string.IsNullOrWhiteSpace(text));
            var imageParts = item.Content
                .Where(content => content.Type == "output_image" && !string.IsNullOrWhiteSpace(content.ImageUrl))
                .Select(content => $"![image]({content.ImageUrl})");
            var videoParts = item.Content
                .Where(content => content.Type == "output_video" && !string.IsNullOrWhiteSpace(content.ImageUrl))
                .Select(content => $"![video]({content.ImageUrl})");

            var combined = string.Join("\n", textParts.Concat(imageParts).Concat(videoParts));
            return new(
                Type: item.Type,
                Role: item.Role,
                Content: combined,
                Id: item.Id,
                CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        return new(
            Type: item.Type,
            Role: item.Role,
            Content: item.Output ?? item.Arguments,
            Id: item.Id,
            CreatedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    private static ChatRole ToChatRole(string? role)
        => role?.ToLowerInvariant() switch
        {
            "system" or "developer" => ChatRole.System,
            "assistant" => ChatRole.Assistant,
            "tool" or "function" => ChatRole.Tool,
            _ => ChatRole.User
        };

    private static string ResolveOpenAiRole(ChatRole role)
    {
        if (role == ChatRole.System)
        {
            return "system";
        }

        if (role == ChatRole.Assistant)
        {
            return "assistant";
        }

        return role == ChatRole.Tool ? "tool" : "user";
    }

    private static int ToInt(long? value)
        => value is null
            ? 0
            : value > int.MaxValue
                ? int.MaxValue
                : (int)value.Value;

    private static string GetResponseContractPrompt(VirtualModelOptions profile)
        => VirtualModelResponseContracts.RequiresYardlyJson(profile)
            ? YardlyJsonContractPrompt
            : NaturalLanguageContractPrompt;

    private static string StripJsonCodeFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstLineEnd = trimmed.IndexOf('\n');
        if (firstLineEnd < 0)
        {
            return trimmed;
        }

        var withoutOpeningFence = trimmed[(firstLineEnd + 1)..].Trim();
        if (withoutOpeningFence.EndsWith("```", StringComparison.Ordinal))
        {
            withoutOpeningFence = withoutOpeningFence[..^3].Trim();
        }

        return withoutOpeningFence;
    }

    private static bool IsYardlyJsonObject(string content)
    {
        try
        {
            using var json = JsonDocument.Parse(content);
            return json.RootElement.ValueKind == JsonValueKind.Object &&
                   json.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) &&
                   string.Equals(schemaVersion.GetString(), "yardly.response.v1", StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string CreateYardlyFallbackJson(string content)
    {
        var summary = string.IsNullOrWhiteSpace(content)
            ? "I need more information to provide Yardly guidance."
            : content.Trim();
        var responseType = summary.Contains("photo", StringComparison.OrdinalIgnoreCase) ||
                           summary.Contains("more information", StringComparison.OrdinalIgnoreCase)
            ? "clarification"
            : "general_yardly";

        var envelope = new Dictionary<string, object?>
        {
            ["schemaVersion"] = "yardly.response.v1",
            ["responseType"] = responseType,
            ["summary"] = summary,
            ["confidence"] = 0.0,
            ["observations"] = Array.Empty<object>(),
            ["possibleIssues"] = Array.Empty<object>(),
            ["carePlan"] = Array.Empty<object>(),
            ["followUpQuestions"] = Array.Empty<string>(),
            ["safetyNotes"] = Array.Empty<string>(),
            ["ui"] = new Dictionary<string, object?>
            {
                ["cards"] = Array.Empty<object>(),
                ["actions"] = Array.Empty<object>()
            }
        };

        return JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
