using System.Collections.Generic;
using Blaze.LlmGateway.Core.TaskRouting;
using Blaze.LlmGateway.Infrastructure.TaskClassification;
using Microsoft.Extensions.AI;
using Xunit;

namespace Blaze.LlmGateway.Tests;

public class TaskClassificationHelperTests
{
    private static List<ChatMessage> MessagesWithMedia(string mediaType) =>
    [
        new ChatMessage(ChatRole.User,
        [
            new TextContent("look at this"),
            new DataContent(new byte[] { 0x01, 0x02 }, mediaType)
        ])
    ];

    [Theory]
    [InlineData("image/png", TaskType.VisionObjectDetection)]
    [InlineData("video/mp4", TaskType.VisionObjectDetection)]
    [InlineData("audio/wav", TaskType.Speech)]
    [InlineData("audio/mpeg", TaskType.Speech)]
    public void General_UpgradesByMediaType(string mediaType, TaskType expected)
    {
        var result = TaskClassificationHelper.ReclassifyForMedia(TaskType.General, MessagesWithMedia(mediaType));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void VisualMedia_WinsOverAudio_WhenBothPresent()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User,
            [
                new DataContent(new byte[] { 0x01 }, "audio/wav"),
                new DataContent(new byte[] { 0x02 }, "image/jpeg")
            ])
        };

        var result = TaskClassificationHelper.ReclassifyForMedia(TaskType.General, messages);
        Assert.Equal(TaskType.VisionObjectDetection, result);
    }

    [Fact]
    public void NonGeneral_Classification_IsUnchanged()
    {
        var result = TaskClassificationHelper.ReclassifyForMedia(TaskType.Coding, MessagesWithMedia("audio/wav"));
        Assert.Equal(TaskType.Coding, result);
    }

    [Fact]
    public void TextOnly_StaysGeneral()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "hello") };
        var result = TaskClassificationHelper.ReclassifyForMedia(TaskType.General, messages);
        Assert.Equal(TaskType.General, result);
    }
}
