using System.Diagnostics;
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Api.Auth;
using Blaze.LlmGateway.Core.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Api.UsageTracking;

/// <summary>
/// P1.1 usage ledger: records every chat request (tokens, latency, cost, API key)
/// into <see cref="IProtocolStore"/>. Wraps the unkeyed router client so all /v1
/// traffic is captured regardless of provider. Ledger failures never fail the request.
/// </summary>
public sealed class UsageTrackingChatClient(
    IChatClient innerClient,
    IProtocolStore store,
    IHttpContextAccessor httpContextAccessor,
    IProviderCatalog? catalog,
    ILogger<UsageTrackingChatClient> logger) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var messages = chatMessages.ToList();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await InnerClient.GetResponseAsync(messages, options, cancellationToken);
            stopwatch.Stop();
            await RecordAsync(
                options,
                response.ModelId,
                promptTokens: (int?)response.Usage?.InputTokenCount ?? EstimateTokens(messages),
                completionTokens: (int?)response.Usage?.OutputTokenCount ?? EstimateTokens(response.Text),
                stopwatch.ElapsedMilliseconds,
                status: "ok",
                streamed: false);
            return response;
        }
        catch (Exception) when (stopwatch.IsRunning)
        {
            stopwatch.Stop();
            await RecordAsync(options, null, EstimateTokens(messages), 0, stopwatch.ElapsedMilliseconds, "error", streamed: false);
            throw;
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamingImpl(chatMessages.ToList(), options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamingImpl(
        List<ChatMessage> messages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        long? promptTokens = null;
        long? completionTokens = null;
        var textLength = 0;
        string? providerModel = null;
        var status = "ok";

        await using var enumerator = InnerClient
            .GetStreamingResponseAsync(messages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;
            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch
            {
                status = "error";
                stopwatch.Stop();
                await RecordAsync(
                    options,
                    providerModel,
                    (int?)promptTokens ?? EstimateTokens(messages),
                    (int?)completionTokens ?? textLength / 4,
                    stopwatch.ElapsedMilliseconds,
                    status,
                    streamed: true);
                throw;
            }

            providerModel ??= update.ModelId;
            textLength += update.Text?.Length ?? 0;
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                promptTokens = usage.Details.InputTokenCount ?? promptTokens;
                completionTokens = usage.Details.OutputTokenCount ?? completionTokens;
            }

            yield return update;
        }

        stopwatch.Stop();
        await RecordAsync(
            options,
            providerModel,
            (int?)promptTokens ?? EstimateTokens(messages),
            (int?)completionTokens ?? textLength / 4,
            stopwatch.ElapsedMilliseconds,
            status,
            streamed: true);
    }

    private async Task RecordAsync(
        ChatOptions? options,
        string? providerModel,
        int promptTokens,
        int completionTokens,
        long latencyMs,
        string status,
        bool streamed)
    {
        try
        {
            var apiKey = httpContextAccessor.HttpContext?.Items[ApiKeyAuthentication.ApiKeyItem] as AdminApiKey;
            var model = options?.ModelId ?? "unknown";
            var totalTokens = promptTokens + completionTokens;

            await store.AddUsageAsync(new UsageRecord(
                Ids.New("usage"),
                DateTimeOffset.UtcNow,
                apiKey?.Id,
                model,
                providerModel,
                promptTokens,
                completionTokens,
                totalTokens,
                ResolveCost(model, providerModel, totalTokens),
                latencyMs,
                status,
                streamed));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record usage for model {Model}", options?.ModelId);
        }
    }

    private decimal ResolveCost(string model, string? providerModel, int totalTokens)
    {
        if (catalog is null || totalTokens <= 0)
        {
            return 0m;
        }

        var deployment = catalog.GetAllDeployments().FirstOrDefault(d =>
            MatchesModel(d.ModelName, model) || MatchesModel(d.Model, model)
            || (providerModel is not null && (MatchesModel(d.ModelName, providerModel) || MatchesModel(d.Model, providerModel))));

        return deployment is null ? 0m : (decimal)(deployment.CostPerToken * totalTokens);
    }

    private static bool MatchesModel(string? candidate, string model)
        => !string.IsNullOrEmpty(candidate) && string.Equals(candidate, model, StringComparison.OrdinalIgnoreCase);

    private static int EstimateTokens(IEnumerable<ChatMessage> messages)
        => messages.Sum(message => (message.Text?.Length ?? 0)) / 4;

    private static int EstimateTokens(string? text)
        => (text?.Length ?? 0) / 4;
}
