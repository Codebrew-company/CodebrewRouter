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
    /// Upgrades a <see cref="TaskType.General"/> classification to
    /// <see cref="TaskType.VisionObjectDetection"/> when the conversation carries image or
    /// video content. Non-General classifications are returned unchanged.
    /// </summary>
    public static TaskType ReclassifyForVision(TaskType taskType, IEnumerable<ChatMessage> messages)
    {
        if (taskType != TaskType.General)
        {
            return taskType;
        }

        var hasVisualMedia = messages.Any(m => m.Contents?.Any(c =>
            (c is DataContent dc && dc.MediaType is not null &&
             (dc.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
              dc.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase))) ||
            (c is UriContent uc && uc.MediaType is not null &&
             (uc.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ||
              uc.MediaType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)))) == true);

        return hasVisualMedia ? TaskType.VisionObjectDetection : taskType;
    }
}
