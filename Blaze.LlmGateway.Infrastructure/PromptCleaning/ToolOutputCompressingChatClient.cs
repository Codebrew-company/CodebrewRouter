using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.PromptCleaning;

/// <summary>
/// Applies <see cref="ToolOutputCompressor"/> to tool-role message content before
/// provider dispatch (so the saving reaches every downstream provider). Fail-open:
/// original messages pass through on any error or when disabled.
/// </summary>
public sealed class ToolOutputCompressingChatClient(
    IChatClient innerClient,
    IOptionsMonitor<LlmGatewayOptions> options,
    ILogger<ToolOutputCompressingChatClient> logger) : DelegatingChatClient(innerClient)
{
    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
        => InnerClient.GetResponseAsync(Apply(chatMessages), chatOptions, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? chatOptions = null,
        CancellationToken cancellationToken = default)
        => InnerClient.GetStreamingResponseAsync(Apply(chatMessages), chatOptions, cancellationToken);

    private IEnumerable<ChatMessage> Apply(IEnumerable<ChatMessage> messages)
    {
        var compressionOptions = options.CurrentValue.ToolOutputCompression;
        if (!compressionOptions.Enabled)
        {
            return messages;
        }

        try
        {
            var list = messages.ToList();
            var totalSaved = 0;
            for (var i = 0; i < list.Count; i++)
            {
                var message = list[i];
                if (message.Role != ChatRole.Tool)
                {
                    continue;
                }

                var replaced = CompressMessage(message, compressionOptions, ref totalSaved);
                if (replaced is not null)
                {
                    list[i] = replaced;
                }
            }

            if (totalSaved > 0)
            {
                logger.LogInformation("[RTK] tool-output compression saved {SavedChars} chars (~{SavedTokens} tokens)",
                    totalSaved, totalSaved / 4);
            }

            return list;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RTK] compression failed; forwarding original messages");
            return messages;
        }
    }

    private static ChatMessage? CompressMessage(ChatMessage message, ToolOutputCompressionOptions compressionOptions, ref int totalSaved)
    {
        var changed = false;
        var newContents = new List<AIContent>(message.Contents.Count);

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    {
                        var result = ToolOutputCompressor.Compress(text.Text, compressionOptions);
                        if (result.SavedChars > 0)
                        {
                            totalSaved += result.SavedChars;
                            changed = true;
                            newContents.Add(new TextContent(result.Text));
                        }
                        else
                        {
                            newContents.Add(content);
                        }

                        break;
                    }

                case FunctionResultContent { Result: string resultText } functionResult when !string.IsNullOrEmpty(resultText):
                    {
                        var result = ToolOutputCompressor.Compress(resultText, compressionOptions);
                        if (result.SavedChars > 0)
                        {
                            totalSaved += result.SavedChars;
                            changed = true;
                            newContents.Add(new FunctionResultContent(functionResult.CallId, result.Text));
                        }
                        else
                        {
                            newContents.Add(content);
                        }

                        break;
                    }

                default:
                    newContents.Add(content);
                    break;
            }
        }

        return changed ? new ChatMessage(message.Role, newContents) { AuthorName = message.AuthorName } : null;
    }
}
