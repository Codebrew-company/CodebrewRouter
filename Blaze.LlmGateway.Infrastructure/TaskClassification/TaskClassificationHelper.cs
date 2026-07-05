using Blaze.LlmGateway.Core.TaskRouting;
using Microsoft.Extensions.AI;

namespace Blaze.LlmGateway.Infrastructure.TaskClassification;

/// <summary>
/// Shared task-classification post-processing used by both <c>CodebrewRouterChatClient</c>
/// and <c>FusionChatClient</c>.
/// </summary>
public static class TaskClassificationHelper
{
    /// <summary>
    /// Upgrades a <see cref="TaskType.General"/> classification based on attached media:
    /// image/video content → <see cref="TaskType.VisionObjectDetection"/>,
    /// audio content → <see cref="TaskType.Speech"/>. Non-General classifications are
    /// returned unchanged. Visual media wins when both are present (vision models
    /// handle mixed-media turns better than audio-only ones).
    /// </summary>
    public static TaskType ReclassifyForMedia(TaskType taskType, IEnumerable<ChatMessage> messages)
    {
        if (taskType != TaskType.General)
        {
            return taskType;
        }

        var hasVisualMedia = false;
        var hasAudioMedia = false;

        foreach (var message in messages)
        {
            foreach (var content in message.Contents ?? [])
            {
                var mediaType = content switch
                {
                    DataContent dc => dc.MediaType,
                    UriContent uc => uc.MediaType,
                    _ => null
                };

                if (mediaType is null)
                {
                    continue;
                }

                if (mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
                    mediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
                {
                    hasVisualMedia = true;
                }
                else if (mediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                {
                    hasAudioMedia = true;
                }
            }
        }

        return hasVisualMedia ? TaskType.VisionObjectDetection
            : hasAudioMedia ? TaskType.Speech
            : taskType;
    }
}
