using System.Runtime.CompilerServices;
using System.Text;
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

public sealed class ProtocolMvpTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ChatCompletions_PreservesContentPartArraysIncludingImages()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "yardly",
              "messages": [
                {
                  "role": "user",
                  "content": [
                    { "type": "text", "text": "What is wrong with this leaf?" },
                    { "type": "image_url", "image_url": { "url": "https://example.com/leaf.png" } },
                    { "type": "input_image", "image_url": "data:image/png;base64,aGVsbG8=" }
                  ]
                }
              ]
            }
            """,
            JsonOptions)!;

        var chatClient = new CapturingChatClient();
        var httpContext = CreateHttpContext();

        await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            httpContext,
            CancellationToken.None);

        chatClient.Messages.Should().ContainSingle();
        var contents = chatClient.Messages[0].Contents;
        contents.OfType<TextContent>().Should().ContainSingle().Which.Text.Should().Be("What is wrong with this leaf?");
        contents.OfType<UriContent>().Should().ContainSingle().Which.Uri.Should().Be(new Uri("https://example.com/leaf.png"));
        contents.OfType<DataContent>().Should().ContainSingle().Which.MediaType.Should().Be("image/png");
    }

    [Fact]
    public async Task ChatCompletions_StreamingSerializesToolCallDeltas()
    {
        var request = JsonSerializer.Deserialize<ChatCompletionRequest>(
            """
            {
              "model": "codebrewRouter",
              "stream": true,
              "messages": [
                { "role": "user", "content": "Use a tool." }
              ],
              "tools": [
                {
                  "type": "function",
                  "function": {
                    "name": "lookup",
                    "parameters": { "type": "object" }
                  }
                }
              ]
            }
            """,
            JsonOptions)!;

        var chatClient = new CapturingChatClient(
            streamingUpdates:
            [
                new ChatResponseUpdate(ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call_1",
                        "lookup",
                        new Dictionary<string, object?> { ["query"] = "weather" })
                ])
                {
                    FinishReason = ChatFinishReason.ToolCalls
                }
            ]);
        var httpContext = CreateHttpContext();

        await ChatCompletionsEndpoint.HandleAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            httpContext,
            CancellationToken.None);

        httpContext.Response.Body.Position = 0;
        var body = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();

        body.Should().Contain("\"tool_calls\"");
        body.Should().Contain("\"id\":\"call_1\"");
        body.Should().Contain("\"name\":\"lookup\"");
        body.Should().Contain("\"finish_reason\":\"tool_calls\"");
        body.Should().Contain("data: [DONE]");
    }

    [Fact]
    public async Task Responses_CreateStoresAndReturnsOpenAiShapedResponse()
    {
        var store = new InMemoryProtocolStore();
        var request = JsonSerializer.Deserialize<CreateResponseRequest>(
            """
            {
              "model": "codebrewRouter",
              "input": "Say hello.",
              "store": true
            }
            """,
            JsonOptions)!;
        var chatClient = new CapturingChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello.")));

        var result = await ResponsesEndpoint.CreateAsync(
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            store,
            CreateHttpContext(),
            CancellationToken.None);

        var response = result.Should().BeOfType<ResponseObject>().Subject;
        response.Object.Should().Be("response");
        response.Status.Should().Be("completed");
        response.OutputText.Should().Be("Hello.");
        response.Output.Should().ContainSingle(item => item.Type == "message");

        var stored = await store.GetResponseAsync(response.Id, CancellationToken.None);
        stored.Should().NotBeNull();
        stored!.OutputText.Should().Be("Hello.");
    }

    [Fact]
    public async Task Conversations_RoundTripsItemsWithOrdering()
    {
        var store = new InMemoryProtocolStore();
        var created = await ConversationsEndpoint.CreateAsync(
            new CreateConversationRequest(Metadata: new Dictionary<string, string> { ["scope"] = "test" }),
            store,
            CancellationToken.None);

        await ConversationsEndpoint.AddItemsAsync(
            created.Id,
            new CreateConversationItemsRequest(
            [
                new ConversationItem("message", "user", "Hello"),
                new ConversationItem("message", "assistant", "Hi")
            ]),
            store,
            CancellationToken.None);

        var listed = await ConversationsEndpoint.ListItemsAsync(created.Id, null, null, "asc", store, CancellationToken.None);

        listed.Data.Select(item => item.Content).Should().Equal("Hello", "Hi");
    }

    [Fact]
    public async Task A2A_SendPersistsTaskAndArtifact()
    {
        var store = new InMemoryProtocolStore();
        var request = JsonSerializer.Deserialize<A2ASendMessageRequest>(
            """
            {
              "message": {
                "messageId": "msg_1",
                "role": "user",
                "parts": [
                  { "kind": "text", "text": "Hello agent" }
                ]
              }
            }
            """,
            JsonOptions)!;
        var chatClient = new CapturingChatClient(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello from A2A.")));

        var task = await A2AEndpoint.SendMessageAsync(
            "codebrewRouter",
            request,
            chatClient,
            new FixedModelSelectionResolver(null),
            Options.Create(new LlmGatewayOptions()),
            store,
            CreateHttpContext(),
            CancellationToken.None);

        task.AgentName.Should().Be("codebrewRouter");
        task.Status.State.Should().Be("completed");
        task.Artifacts.Should().ContainSingle().Which.Parts[0].Text.Should().Be("Hello from A2A.");

        var stored = await store.GetA2ATaskAsync(task.Id, CancellationToken.None);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task JsonProtocolStore_PersistsResponsesConversationsAndA2ATasksAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"codebrewrouter-protocol-{Guid.NewGuid():N}.json");
        try
        {
            var firstStore = new JsonProtocolStore(path);
            var response = new ResponseObject(
                Id: "resp_test",
                Object: "response",
                CreatedAt: 1,
                Status: "completed",
                Model: "codebrewRouter",
                Output:
                [
                    new ResponseOutputItem(
                        Id: "msg_test",
                        Type: "message",
                        Status: "completed",
                        Role: "assistant",
                        Content: [new ResponseContentPart("output_text", "persisted")])
                ],
                OutputText: "persisted");
            var conversation = ConversationObject.Create("conv_test");
            var task = new A2ATask(
                Id: "task_test",
                AgentName: "codebrewRouter",
                Status: new A2ATaskStatus("completed", DateTimeOffset.UtcNow),
                Artifacts: [new A2AArtifact("artifact_test", "response", [new A2APart("text", "persisted")])],
                CreatedAt: DateTimeOffset.UtcNow);

            await firstStore.SaveResponseAsync(response, CancellationToken.None);
            await firstStore.SaveConversationAsync(conversation, CancellationToken.None);
            await firstStore.AddConversationItemsAsync(conversation.Id, [new ConversationItem("message", "user", "hello")], CancellationToken.None);
            await firstStore.SaveA2ATaskAsync(task, CancellationToken.None);

            var secondStore = new JsonProtocolStore(path);

            (await secondStore.GetResponseAsync(response.Id, CancellationToken.None))!.OutputText.Should().Be("persisted");
            (await secondStore.GetConversationAsync(conversation.Id, CancellationToken.None)).Should().NotBeNull();
            (await secondStore.ListConversationItemsAsync(conversation.Id, cancellationToken: CancellationToken.None))
                .Should().ContainSingle(item => item.Content == "hello");
            (await secondStore.GetA2ATaskAsync(task.Id, CancellationToken.None))!.Artifacts[0].Parts[0].Text.Should().Be("persisted");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IModelAvailabilityRegistry>(new AlwaysAvailableRegistry())
            .BuildServiceProvider();

        return new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() }
        };
    }

    private sealed class CapturingChatClient(
        ChatResponse? response = null,
        IReadOnlyList<ChatResponseUpdate>? streamingUpdates = null) : IChatClient
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
            Messages = chatMessages.ToArray();
            Options = options;

            foreach (var update in streamingUpdates ??
                     [new ChatResponseUpdate(ChatRole.Assistant, "ok") { FinishReason = ChatFinishReason.Stop }])
            {
                await Task.Yield();
                yield return update;
            }
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
