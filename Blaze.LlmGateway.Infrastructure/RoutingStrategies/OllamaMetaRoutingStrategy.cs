using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blaze.LlmGateway.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies;

/// <summary>
/// Meta-routing strategy that delegates routing decisions to a local Ollama "router" model
/// (the keyed <c>OllamaLocal</c> client). Sends the incoming prompt to the router model asking
/// it to classify which provider should handle it. Falls back to <see cref="KeywordRoutingStrategy"/>
/// on any failure.
/// </summary>
public class OllamaMetaRoutingStrategy(
    IChatClient routerClient,
    IRoutingStrategy fallbackStrategy,
    ILogger<OllamaMetaRoutingStrategy> logger) : IRoutingStrategy
{
    private static readonly string[] ValidDestinations = Enum.GetNames<RouteDestination>();

    // Circuit breaker: once Ollama fails, skip subsequent calls for the cooldown window.
    private static readonly TimeSpan CooldownDuration = TimeSpan.FromMinutes(5);
    private DateTimeOffset? _circuitOpenedAt;

    private static readonly string SystemPrompt = $"""
        You are a request router. Based on the user's message, decide which AI provider should handle it.
        Respond with ONLY one of these exact words (no punctuation, no explanation):
        {string.Join(", ", Enum.GetNames<RouteDestination>())}

        Routing guidelines:
        - AzureFoundry: enterprise/business tasks, Office 365, Azure-specific questions, general high-quality chat
        - FoundryLocal: local/private tasks that must stay on this machine (Foundry Local, OpenAI-compatible)
        - GithubModels: code generation, debugging, GitHub-related tasks, inference via GitHub Models
        """;

    public async Task<RouteDestination> ResolveAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        // Circuit-breaker fast-path: if Ollama is in cooldown, skip directly to fallback.
        if (_circuitOpenedAt is { } openedAt && DateTimeOffset.UtcNow - openedAt < CooldownDuration)
        {
            return await fallbackStrategy.ResolveAsync(messages, cancellationToken);
        }

        try
        {
            var lastUserMessage = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? "";
            if (string.IsNullOrWhiteSpace(lastUserMessage))
                return await fallbackStrategy.ResolveAsync(messages, cancellationToken);

            var routingMessages = new[]
            {
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, lastUserMessage)
            };

            var routingOptions = new ChatOptions { MaxOutputTokens = 10, Temperature = 0f };
            
            // Add timeout to prevent hanging on unreachable Ollama instances (primary router probe)
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3)); // 3-second timeout on router probe
            
            // Wrap in explicit timeout task in case CancellationToken doesn't propagate through HTTP stack
            var routerTask = routerClient.GetResponseAsync(routingMessages, routingOptions, timeoutCts.Token);
            var completedTask = await Task.WhenAny(
                routerTask,
                Task.Delay(TimeSpan.FromSeconds(4), cancellationToken)  // 4-second hard limit
            ).ConfigureAwait(false);
            
            if (completedTask != routerTask)
            {
                throw new OperationCanceledException("Router probe exceeded 4-second timeout");
            }
            
            var response = await routerTask;
            var responseText = response.Text?.Trim() ?? "";

            if (Enum.TryParse<RouteDestination>(responseText, ignoreCase: true, out var destination))
            {
                logger.LogInformation("Meta-router selected destination: {Destination}", destination);
                return destination;
            }

            // Try to find a match within the response text
            var match = ValidDestinations.FirstOrDefault(d => responseText.Contains(d, StringComparison.OrdinalIgnoreCase));
            if (match != null && Enum.TryParse<RouteDestination>(match, out var matched))
            {
                logger.LogInformation("Meta-router (partial match) selected destination: {Destination}", matched);
                return matched;
            }

            logger.LogWarning("Meta-router returned unrecognized destination: '{Response}'. Falling back.", responseText);
        }
        catch (OperationCanceledException ex)
        {
            // Timeout or cancellation on router probe — open circuit and fall back
            if (_circuitOpenedAt is null)
            {
                logger.LogWarning(ex, "Meta-router probe timed out (or was cancelled) — opening circuit for {Cooldown}; falling back to keyword strategy.", CooldownDuration);
            }
            _circuitOpenedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            // Open the circuit; log once at warning, subsequent calls will silently fall back.
            if (_circuitOpenedAt is null)
            {
                logger.LogWarning(ex, "Meta-router call failed — opening circuit for {Cooldown}; falling back to keyword strategy.", CooldownDuration);
            }
            _circuitOpenedAt = DateTimeOffset.UtcNow;
        }

        return await fallbackStrategy.ResolveAsync(messages, cancellationToken);
    }
}
