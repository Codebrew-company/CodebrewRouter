using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Blaze.LlmGateway.Api;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Tests;

public sealed class OpenAiCompatibilityTests
{
    private static readonly JsonSerializerOptions OpenAiJsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ChatCompletionRequest_BindsOpenAiSnakeCaseFields()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewRouter",
              "messages": [
                { "role": "developer", "content": "Be concise." },
                { "role": "user", "content": "Hello" }
              ],
              "temperature": 0.2,
              "max_tokens": 64,
              "max_completion_tokens": 128,
              "top_p": 0.7,
              "frequency_penalty": 0.1,
              "presence_penalty": 0.2,
              "parallel_tool_calls": false,
              "stop": ["END", "DONE"],
              "tool_choice": {
                "type": "function",
                "function": { "name": "lookup" }
              },
              "response_format": { "type": "json_object" }
            }
            """,
            OpenAiJsonOptions);

        request.Should().NotBeNull();
        request!.Model.Should().Be("codebrewRouter");
        request.MaxTokens.Should().Be(64);
        request.Temperature.Should().Be(0.2f);
        request.TopP.Should().Be(0.7f);
        request.FrequencyPenalty.Should().Be(0.1f);
        request.PresencePenalty.Should().Be(0.2f);

        GetPropertyValue<int?>(request, "MaxCompletionTokens").Should().Be(128);
        GetPropertyValue<bool?>(request, "ParallelToolCalls").Should().BeFalse();

        var stop = GetPropertyValue<JsonElement?>(request, "Stop");
        stop.Should().NotBeNull();
        stop!.Value.ValueKind.Should().Be(JsonValueKind.Array);
        stop.Value.EnumerateArray().Select(item => item.GetString()).Should().Equal("END", "DONE");

        var toolChoice = GetPropertyValue<JsonElement?>(request, "ToolChoice");
        toolChoice.Should().NotBeNull();
        toolChoice!.Value.GetProperty("function").GetProperty("name").GetString().Should().Be("lookup");

        var responseFormat = GetPropertyValue<JsonElement?>(request, "ResponseFormat");
        responseFormat.Should().NotBeNull();
        responseFormat!.Value.GetProperty("type").GetString().Should().Be("json_object");
    }

    [Fact]
    public void ChatCompletionResponse_SerializesOpenAiSnakeCaseFields()
    {
        var response = new ChatCompletionResponse(
            Id: "chatcmpl-test",
            Object: "chat.completion",
            Created: 1,
            Model: "codebrewRouter",
            Choices:
            [
                new Choice(
                    Index: 0,
                    Message: new ChatMessageDto(Role: "assistant", Content: "ok"),
                    Delta: null,
                    FinishReason: "stop")
            ],
            Usage: new Usage(PromptTokens: 3, CompletionTokens: 2, TotalTokens: 5));

        var json = JsonSerializer.Serialize(response, OpenAiJsonOptions);

        json.Should().Contain("\"finish_reason\"");
        json.Should().Contain("\"prompt_tokens\"");
        json.Should().Contain("\"completion_tokens\"");
        json.Should().Contain("\"total_tokens\"");
        json.Should().NotContain("finishReason");
        json.Should().NotContain("promptTokens");
    }

    [Fact]
    public void ChatMessageDto_RoundTripsOpenAiToolFields()
    {
        var message = JsonSerializer.Deserialize<ChatMessageDto>(
            """
            {
              "role": "assistant",
              "content": "",
              "tool_calls": [
                {
                  "id": "call_1",
                  "type": "function",
                  "function": {
                    "name": "lookup",
                    "arguments": "{\"query\":\"weather\"}"
                  }
                }
              ]
            }
            """,
            OpenAiJsonOptions);

        message.Should().NotBeNull();
        message!.Role.Should().Be("assistant");

        var toolCalls = GetPropertyValue<IEnumerable?>(message, "ToolCalls");
        toolCalls.Should().NotBeNull();
        toolCalls!.Cast<object>().Should().ContainSingle();

        var json = JsonSerializer.Serialize(message, OpenAiJsonOptions);
        json.Should().Contain("\"tool_calls\"");
        json.Should().Contain("\"function\"");
        json.Should().Contain("\"arguments\"");

        var toolMessage = JsonSerializer.Deserialize<ChatMessageDto>(
            """
            {
              "role": "tool",
              "tool_call_id": "call_1",
              "content": "42"
            }
            """,
            OpenAiJsonOptions);

        toolMessage.Should().NotBeNull();
        GetPropertyValue<string?>(toolMessage!, "ToolCallId").Should().Be("call_1");
    }

    [Fact]
    public async Task HandleAsync_MapsOpenAiRequestOptionsAndRolesToMeai()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewRouter",
              "messages": [
                { "role": "developer", "content": "Be concise." },
                { "role": "user", "content": "Hi" },
                { "role": "assistant", "content": "Hello" },
                { "role": "tool", "tool_call_id": "call_1", "content": "42" }
              ],
              "max_tokens": 64,
              "max_completion_tokens": 128,
              "stop": ["END", "DONE"],
              "parallel_tool_calls": false,
              "tool_choice": {
                "type": "function",
                "function": { "name": "lookup" }
              },
              "response_format": { "type": "json_object" }
            }
            """,
            OpenAiJsonOptions)!;

        var chatClient = new CapturingChatClient();
        var services = new ServiceCollection()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };

        await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            httpContext,
            CancellationToken.None);

        chatClient.Messages.Select(message => message.Role)
            .Should().Equal(ChatRole.System, ChatRole.System, ChatRole.User, ChatRole.Assistant, ChatRole.Tool);
        chatClient.Messages[0].Text.Should().Contain("normal conversational Markdown");
        chatClient.Options.Should().NotBeNull();
        chatClient.Options!.MaxOutputTokens.Should().Be(128);
        chatClient.Options.StopSequences.Should().Equal("END", "DONE");
        chatClient.Options.AllowMultipleToolCalls.Should().BeFalse();
        chatClient.Options.ResponseFormat.Should().Be(ChatResponseFormat.Json);

        var requiredFunctionName = chatClient.Options.ToolMode?
            .GetType()
            .GetProperty("RequiredFunctionName")
            ?.GetValue(chatClient.Options.ToolMode);
        requiredFunctionName.Should().Be("lookup");
    }

    [Fact]
    public async Task HandleAsync_RoundTripsOpenAiToolCallsWithoutServerSideInvocation()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewRouter",
              "messages": [
                { "role": "user", "content": "Look up the weather." }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "lookup",
                    "description": "Looks up a value.",
                    "parameters": {
                      "type": "object",
                      "properties": {
                        "query": { "type": "string" }
                      },
                      "required": ["query"]
                    }
                  }
                }
              ],
              "tool_choice": "auto"
            }
            """,
            OpenAiJsonOptions)!;

        var toolCallResponse = new ChatResponse(new ChatMessage(
            ChatRole.Assistant,
            [
                new FunctionCallContent(
                    "call_1",
                    "lookup",
                    new Dictionary<string, object> { ["query"] = "weather" })
            ]))
        {
            FinishReason = ChatFinishReason.ToolCalls
        };
        var chatClient = new CapturingChatClient(toolCallResponse);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };

        var result = await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            httpContext,
            CancellationToken.None);

        chatClient.Options.Should().NotBeNull();
        var tool = chatClient.Options!.Tools.Should().ContainSingle().Subject;
        tool.Should().BeAssignableTo<AIFunctionDeclaration>();
        tool.Should().NotBeAssignableTo<AIFunction>();
        tool.Name.Should().Be("lookup");

        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var choice = json.RootElement.GetProperty("choices")[0];
        choice.GetProperty("finish_reason").GetString().Should().Be("tool_calls");
        var toolCall = choice.GetProperty("message").GetProperty("tool_calls")[0];
        toolCall.GetProperty("id").GetString().Should().Be("call_1");
        toolCall.GetProperty("type").GetString().Should().Be("function");
        toolCall.GetProperty("function").GetProperty("name").GetString().Should().Be("lookup");
        toolCall.GetProperty("function").GetProperty("arguments").GetString()
            .Should().Contain("\"query\":\"weather\"");
    }

    [Fact]
    public async Task HandleAsync_PrependsConfiguredVirtualModelSystemPrompt()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewSharpClient",
              "messages": [
                { "role": "user", "content": "How should I structure this MAUI service?" }
              ]
            }
            """,
            OpenAiJsonOptions)!;

        var chatClient = new CapturingChatClient();
        var services = new ServiceCollection()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext { RequestServices = services };
        var options = Options.Create(new LlmGatewayOptions
        {
            VirtualModels =
            {
                ["codebrewSharpClient"] = new VirtualModelOptions
                {
                    ModelId = "codebrewSharpClient",
                    SystemPrompt = "You are a focused C# assistant.",
                    FallbackRules =
                    {
                        ["General"] = ["LocalGemma"]
                    }
                }
            }
        });

        await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            options,
            httpContext,
            CancellationToken.None);

        chatClient.Messages.Should().HaveCount(3);
        chatClient.Messages[0].Role.Should().Be(ChatRole.System);
        chatClient.Messages[0].Text.Should().Be("You are a focused C# assistant.");
        chatClient.Messages[1].Role.Should().Be(ChatRole.System);
        chatClient.Messages[1].Text.Should().Contain("normal conversational Markdown");
        chatClient.Messages[2].Role.Should().Be(ChatRole.User);
        chatClient.Options.Should().NotBeNull();
        chatClient.Options!.ModelId.Should().Be("codebrewSharpClient");
        chatClient.Options.ResponseFormat.Should().BeNull();
    }

    [Fact]
    public async Task HandleAsync_ForYardlyForcesJsonContractAndNormalizesPlainTextResponse()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "yardly",
              "messages": [
                { "role": "user", "content": "What is wrong with this tomato leaf?" }
              ]
            }
            """,
            OpenAiJsonOptions)!;

        var chatClient = new CapturingChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "The tomato leaf may be stressed by inconsistent watering."))
            {
                FinishReason = ChatFinishReason.Stop
            });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        var options = Options.Create(new LlmGatewayOptions
        {
            VirtualModels =
            {
                ["yardly"] = new VirtualModelOptions
                {
                    ModelId = "yardly",
                    ResponseContract = VirtualModelResponseContracts.YardlyJson,
                    SystemPrompt = "You are Yardly, a plant identification and care assistant.",
                    FallbackRules =
                    {
                        ["General"] = ["LocalGemma"]
                    }
                }
            }
        });

        var result = await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            options,
            httpContext,
            CancellationToken.None);

        chatClient.Options.Should().NotBeNull();
        chatClient.Options!.ResponseFormat.Should().Be(ChatResponseFormat.Json);
        chatClient.Messages.Select(message => message.Text ?? string.Empty)
            .Should().Contain(text => text.Contains("yardly.response.v1", StringComparison.Ordinal));

        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var content = json.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        content.Should().NotBeNullOrWhiteSpace();
        using var yardlyJson = JsonDocument.Parse(content!);
        yardlyJson.RootElement.GetProperty("schemaVersion").GetString().Should().Be("yardly.response.v1");
        yardlyJson.RootElement.GetProperty("summary").GetString()
            .Should().Contain("tomato leaf");
    }

    [Fact]
    public async Task HandleAsync_ForPlannerKeepsNaturalLanguageResponse()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewPlanner",
              "messages": [
                { "role": "user", "content": "Make a plan for the router output contract." }
              ]
            }
            """,
            OpenAiJsonOptions)!;

        var chatClient = new CapturingChatClient(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Plan:\n1. Keep dev tools conversational.\n2. Make Yardly JSON."))
            {
                FinishReason = ChatFinishReason.Stop
            });
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
        var options = Options.Create(new LlmGatewayOptions
        {
            VirtualModels =
            {
                ["codebrewPlanner"] = new VirtualModelOptions
                {
                    ModelId = "codebrewPlanner",
                    ResponseContract = VirtualModelResponseContracts.NaturalLanguage,
                    SystemPrompt = "You are CodebrewPlanner.",
                    FallbackRules =
                    {
                        ["General"] = ["LocalGemma"]
                    }
                }
            }
        });

        var result = await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            options,
            httpContext,
            CancellationToken.None);

        chatClient.Options.Should().NotBeNull();
        chatClient.Options!.ResponseFormat.Should().BeNull();

        await result.ExecuteAsync(httpContext);
        httpContext.Response.Body.Position = 0;
        using var json = await JsonDocument.ParseAsync(httpContext.Response.Body);
        var content = json.RootElement.GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        content.Should().Be("Plan:\n1. Keep dev tools conversational.\n2. Make Yardly JSON.");
    }

    private static T? GetPropertyValue<T>(object instance, string propertyName)
    {
        var property = instance.GetType().GetProperty(propertyName);
        property.Should().NotBeNull($"OpenAI DTOs should expose {propertyName}");
        return property is null ? default : (T?)property.GetValue(instance);
    }

    private sealed class CapturingChatClient(ChatResponse? response = null) : IChatClient
    {
        public IReadOnlyList<ChatMessage> Messages { get; private set; } = [];

        public ChatOptions? Options { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Messages = chatMessages.ToArray();
            Options = options;

            return Task.FromResult(response ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> chatMessages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok")
            {
                FinishReason = ChatFinishReason.Stop
            };
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    private sealed class FixedModelSelectionResolver(IChatClient? client) : IModelSelectionResolver
    {
        public Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
            => Task.FromResult(client);
    }

    private sealed class AlwaysAvailableRegistry : IModelAvailabilityRegistry
    {
        public IReadOnlyList<AvailableModel> GetModels(bool includeUnavailable = false) => [];

        public AvailableModel? FindModel(string modelId, bool includeUnavailable = false) => null;

        public bool IsProviderAvailable(string provider) => true;

        public string? GetProviderError(string provider) => null;
    }
}
