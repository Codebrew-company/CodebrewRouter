using System.Net;
using System.Text;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Prove-It regression: the FoundryLocal provider must talk to the configured
/// endpoint using OpenAI-shaped request paths (/v1/chat/completions). The previous
/// implementation registered FoundryLocal with AzureOpenAIClient, which built
/// Azure-style /openai/deployments/{model}/chat/completions paths and 404'd against
/// Aspire's Foundry Local emulator endpoint.
/// </summary>
public sealed class FoundryLocalProviderShapeTests
{
    [Fact]
    public async Task FoundryLocalChatClient_UsesOpenAiCompatibleRequestPath()
    {
        // Bind a short-lived HTTP listener on a random localhost port and capture
        // the first request path the FoundryLocal IChatClient sends.
        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var capturedPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                capturedPath.TrySetResult(ctx.Request.Url!.AbsolutePath);

                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                ctx.Response.ContentType = "application/json";
                var body = """
                    {"id":"x","object":"chat.completion","created":1,"model":"phi","choices":[
                      {"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                    """;
                var buffer = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = buffer.Length;
                await ctx.Response.OutputStream.WriteAsync(buffer);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                capturedPath.TrySetException(ex);
            }
        });

        var endpoint = $"http://127.0.0.1:{port}/v1";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:Providers:FoundryLocal:Endpoint"] = endpoint,
                ["LlmGateway:Providers:FoundryLocal:ApiKey"] = "notneeded",
                ["LlmGateway:Providers:FoundryLocal:Model"] = "phi-4-mini",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<LlmGatewayOptions>(config.GetSection("LlmGateway"));
        services.AddLogging();
        services.AddLlmProviders();
        services.AddSingleton(new Mock<ITokenCounter>().Object);
        services.AddSingleton(new Mock<IContextCompactor>().Object);
        services.AddSingleton(Options.Create(new ContextSizingOptions()));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>("FoundryLocal");

        // Fire one ping; we only care about the request path the client emits.
        try
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1 },
                CancellationToken.None);
        }
        catch
        {
            // We don't care if response parsing rejects the canned body — only the path.
        }

        var path = await capturedPath.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("/v1/chat/completions", path);
        Assert.DoesNotContain("/openai/deployments", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LmStudioChatClient_UsesOpenAiCompatibleRequestPath()
    {
        using var listener = new HttpListener();
        var port = GetFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(prefix);
        listener.Start();

        var capturedPath = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                capturedPath.TrySetResult(ctx.Request.Url!.AbsolutePath);

                ctx.Response.StatusCode = (int)HttpStatusCode.OK;
                ctx.Response.ContentType = "application/json";
                var body = """
                    {"id":"x","object":"chat.completion","created":1,"model":"local-model","choices":[
                      {"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],
                     "usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}
                    """;
                var buffer = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = buffer.Length;
                await ctx.Response.OutputStream.WriteAsync(buffer);
                ctx.Response.OutputStream.Close();
            }
            catch (Exception ex)
            {
                capturedPath.TrySetException(ex);
            }
        });

        var endpoint = $"http://127.0.0.1:{port}/v1";
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["LlmGateway:Providers:LmStudio:Endpoint"] = endpoint,
                ["LlmGateway:Providers:LmStudio:Model"] = "local-model",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.Configure<LlmGatewayOptions>(config.GetSection("LlmGateway"));
        services.AddLogging();
        services.AddLlmProviders();
        services.AddSingleton(new Mock<ITokenCounter>().Object);
        services.AddSingleton(new Mock<IContextCompactor>().Object);
        services.AddSingleton(Options.Create(new ContextSizingOptions()));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>("LmStudio");

        try
        {
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                new ChatOptions { MaxOutputTokens = 1 },
                CancellationToken.None);
        }
        catch
        {
        }

        var path = await capturedPath.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("/v1/chat/completions", path);
        Assert.DoesNotContain("/openai/deployments", path, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
