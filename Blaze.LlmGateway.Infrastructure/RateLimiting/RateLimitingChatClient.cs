using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.RateLimiting;

/// <summary>
/// Middleware that rate-limits requests per deployment using a token bucket.
/// Wraps an <see cref="IChatClient"/> and enforces per-deployment rate limits
/// for both request count and token consumption.
///
/// When the bucket is exhausted the middleware throws
/// <see cref="RateLimitExceededException"/>, which the upstream router can handle
/// by falling back to another deployment or returning a 429-like response.
/// </summary>
public sealed class RateLimitingChatClient : DelegatingChatClient
{
    private readonly RateLimitBucket _bucket;
    private readonly string _deploymentName;
    private readonly ILogger<RateLimitingChatClient> _logger;

    public RateLimitingChatClient(
        IChatClient innerClient,
        RateLimitBucket bucket,
        string deploymentName,
        ILogger<RateLimitingChatClient> logger)
        : base(innerClient)
    {
        _bucket = bucket ?? throw new ArgumentNullException(nameof(bucket));
        _deploymentName = deploymentName ?? throw new ArgumentNullException(nameof(deploymentName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Check request rate limit
        if (!_bucket.TryConsumeRequest())
        {
            _logger.LogWarning(
                "Rate limit hit for deployment {DeploymentName}: request bucket exhausted",
                _deploymentName);
            throw new RateLimitExceededException(_deploymentName, "request");
        }

        var response = await InnerClient.GetResponseAsync(chatMessages, options, cancellationToken);

        // Record token consumption
        var outputTokens = response.Usage?.OutputTokenCount
            ?? response.Usage?.TotalTokenCount
            ?? 0;
        if (outputTokens > 0)
        {
            var reserved = _bucket.TryReserveTokens((int)outputTokens);
            if (reserved < outputTokens)
            {
                _logger.LogDebug(
                    "Token bucket for deployment {DeploymentName}: reserved {Reserved}/{Requested} tokens",
                    _deploymentName, reserved, outputTokens);
            }
        }

        return response;
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => StreamingImpl(chatMessages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> StreamingImpl(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Check request rate limit
        if (!_bucket.TryConsumeRequest())
        {
            _logger.LogWarning(
                "Rate limit hit for deployment {DeploymentName}: request bucket exhausted",
                _deploymentName);
            throw new RateLimitExceededException(_deploymentName, "request");
        }

        await using var enumerator = InnerClient
            .GetStreamingResponseAsync(chatMessages, options, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        var chunkCount = 0;
        var tokenEstimate = 0;

        while (true)
        {
            bool hasMore;
            try
            {
                hasMore = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                throw;
            }

            if (!hasMore)
                break;

            chunkCount++;
            var chunk = enumerator.Current;
            // Rough token estimate from text length (approx 1 token per 4 chars for English)
            if (!string.IsNullOrEmpty(chunk.Text))
            {
                tokenEstimate += Math.Max(1, chunk.Text.Length / 4);
            }

            yield return chunk;
        }

        // Apply token rate limit after streaming completes
        if (tokenEstimate > 0)
        {
            var reserved = _bucket.TryReserveTokens(tokenEstimate);
            if (reserved < tokenEstimate)
            {
                _logger.LogDebug(
                    "Token bucket for deployment {DeploymentName}: streamed ~{Estimated} tokens, reserved {Reserved}",
                    _deploymentName, tokenEstimate, reserved);
            }
        }
    }
}

/// <summary>
/// Thrown when a rate limit is exceeded for a deployment.
/// </summary>
public sealed class RateLimitExceededException : InvalidOperationException
{
    public string DeploymentName { get; }
    public string LimitType { get; }

    public RateLimitExceededException(string deploymentName, string limitType)
        : base($"Rate limit exceeded for deployment '{deploymentName}' ({limitType}).")
    {
        DeploymentName = deploymentName;
        LimitType = limitType;
    }
}
