using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blaze.LlmGateway.Api;

/// <summary>
/// Custom JSON converter that allows ChatMessageDto.Content to be either a string (simple text)
/// or an array of content parts (text, images, audio) for multimodal support.
/// </summary>
public class ChatMessageContentConverter : JsonConverter<ChatMessageDto>
{
    public override ChatMessageDto Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var jsonDoc = JsonDocument.ParseValue(ref reader);
        var root = jsonDoc.RootElement;

        if (!root.TryGetProperty("role", out var roleElement))
            throw new JsonException("Missing required 'role' field");

        var role = roleElement.GetString() ?? throw new JsonException("Role must be a string");

        // Content can be either a string or an array of content parts.
        string content = "";
        IList<ChatContentPart>? contentParts = null;
        if (root.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                content = contentElement.GetString() ?? "";
            }
            else if (contentElement.ValueKind == JsonValueKind.Array)
            {
                contentParts = [];
                var textParts = new List<string>();
                foreach (var part in contentElement.EnumerateArray())
                {
                    var parsed = ReadContentPart(part);
                    if (parsed is null)
                    {
                        continue;
                    }

                    contentParts.Add(parsed);
                    if (!string.IsNullOrEmpty(parsed.Text))
                    {
                        textParts.Add(parsed.Text);
                    }
                }

                content = string.Join("\n", textParts);
            }
        }

        string? name = null;
        if (root.TryGetProperty("name", out var nameElement) &&
            nameElement.ValueKind == JsonValueKind.String)
        {
            name = nameElement.GetString();
        }

        string? toolCallId = null;
        if (root.TryGetProperty("tool_call_id", out var toolCallIdElement) &&
            toolCallIdElement.ValueKind == JsonValueKind.String)
        {
            toolCallId = toolCallIdElement.GetString();
        }

        IList<ToolCallDto>? toolCalls = null;
        if (root.TryGetProperty("tool_calls", out var toolCallsElement) &&
            toolCallsElement.ValueKind == JsonValueKind.Array)
        {
            toolCalls = toolCallsElement.Deserialize<IList<ToolCallDto>>(options);
        }

        return new ChatMessageDto(role, content, name, toolCallId, toolCalls, contentParts);
    }

    public override void Write(Utf8JsonWriter writer, ChatMessageDto value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("role", value.Role);
        writer.WritePropertyName("content");
        if (value.ContentParts is { Count: > 0 })
        {
            WriteContentParts(writer, value.ContentParts);
        }
        else
        {
            writer.WriteStringValue(value.Content);
        }

        if (!string.IsNullOrWhiteSpace(value.Name))
        {
            writer.WriteString("name", value.Name);
        }

        if (!string.IsNullOrWhiteSpace(value.ToolCallId))
        {
            writer.WriteString("tool_call_id", value.ToolCallId);
        }

        if (value.ToolCalls is not null)
        {
            writer.WritePropertyName("tool_calls");
            JsonSerializer.Serialize(writer, value.ToolCalls, options);
        }

        writer.WriteEndObject();
    }

    private static ChatContentPart? ReadContentPart(JsonElement part)
    {
        if (part.ValueKind != JsonValueKind.Object ||
            !part.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var type = typeElement.GetString() ?? "";
        if (type is "text" or "input_text")
        {
            return new ChatContentPart(
                Type: type,
                Text: ReadString(part, "text"));
        }

        if (type is "image_url" or "input_image")
        {
            var imageUrl = ReadImageUrl(part, out var detail);
            return string.IsNullOrWhiteSpace(imageUrl)
                ? null
                : new ChatContentPart(
                    Type: type,
                    ImageUrl: imageUrl,
                    Detail: detail,
                    MediaType: InferMediaType(imageUrl));
        }

        if (type is "file" or "input_file")
        {
            return new ChatContentPart(
                Type: type,
                FileId: ReadString(part, "file_id"),
                FileName: ReadString(part, "filename") ?? ReadString(part, "file_name"));
        }

        return new ChatContentPart(Type: type, Text: ReadString(part, "text"));
    }

    private static string? ReadImageUrl(JsonElement part, out string? detail)
    {
        detail = ReadString(part, "detail");
        if (!part.TryGetProperty("image_url", out var imageUrl))
        {
            return ReadString(part, "url");
        }

        if (imageUrl.ValueKind == JsonValueKind.String)
        {
            return imageUrl.GetString();
        }

        if (imageUrl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        detail = ReadString(imageUrl, "detail") ?? detail;
        return ReadString(imageUrl, "url");
    }

    private static string? ReadString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? InferMediaType(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return null;
        }

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
            _ => "image/*"
        };
    }

    private static void WriteContentParts(Utf8JsonWriter writer, IList<ChatContentPart> parts)
    {
        writer.WriteStartArray();
        foreach (var part in parts)
        {
            writer.WriteStartObject();
            writer.WriteString("type", part.Type);

            if (!string.IsNullOrEmpty(part.Text))
            {
                writer.WriteString("text", part.Text);
            }

            if (!string.IsNullOrEmpty(part.ImageUrl))
            {
                writer.WritePropertyName("image_url");
                writer.WriteStartObject();
                writer.WriteString("url", part.ImageUrl);
                if (!string.IsNullOrWhiteSpace(part.Detail))
                {
                    writer.WriteString("detail", part.Detail);
                }

                writer.WriteEndObject();
            }

            if (!string.IsNullOrEmpty(part.FileId))
            {
                writer.WriteString("file_id", part.FileId);
            }

            if (!string.IsNullOrEmpty(part.FileName))
            {
                writer.WriteString("filename", part.FileName);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
