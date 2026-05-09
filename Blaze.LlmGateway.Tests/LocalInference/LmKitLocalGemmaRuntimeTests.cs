using Blaze.LlmGateway.LocalInference;
using FluentAssertions;

namespace Blaze.LlmGateway.Tests.LocalInference;

public sealed class LmKitLocalGemmaRuntimeTests
{
    [Fact]
    public void BuildLoadFailureMessage_WhenNativeBackendRejectsTensorType_ErrorIsActionable()
    {
        var exception = new InvalidOperationException(
            "Failed to load model. native: gguf_init_from_file_impl: tensor 'per_layer_token_embd.weight' has invalid ggml type 40. should be in [0, 40)");

        var message = LmKitLocalGemmaRuntime.BuildLoadFailureMessage("E:\\models\\gemma4.lmk", exception);

        message.Should().Contain("LM-Kit could not load local Gemma model");
        message.Should().Contain("per_layer_token_embd.weight");
        message.Should().Contain("unsupported GGML type 40");
        message.Should().Contain("Update LM-Kit");
    }

    [Fact]
    public void BuildLoadFailureMessage_WhenErrorIsUnrecognized_ReturnsGenericFallback()
    {
        var exception = new InvalidOperationException("General load failure.");

        var message = LmKitLocalGemmaRuntime.BuildLoadFailureMessage("E:\\models\\gemma4.lmk", exception);

        message.Should().Be("Failed to load local Gemma model from 'E:\\models\\gemma4.lmk' via LM-Kit.");
    }
}
