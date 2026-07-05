using System.Collections.Concurrent;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Api.Auth;

/// <summary>
/// Default-deny API-key middleware over the whole /v1 path prefix.
/// Every current and future /v1 endpoint is protected automatically —
/// the inverse of 9router's allowlist mistake (CVE-2026-46339).
/// Also enforces per-key (or per-socket-IP) request rate limits (CVE-2026-55501:
/// identity never comes from X-Forwarded-For).
/// </summary>
public static class ApiKeyAuthentication
{
    /// <summary>HttpContext.Items key carrying the validated <see cref="AdminApiKey"/>.</summary>
    public const string ApiKeyItem = "Gateway.ApiKey";

    private static readonly ConcurrentDictionary<string, RateLimitBucket> Buckets = new(StringComparer.Ordinal);

    public static void UseV1ApiKeyAuth(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/v1"))
            {
                await next(context);
                return;
            }

            var options = context.RequestServices
                .GetRequiredService<IOptionsMonitor<LlmGatewayOptions>>().CurrentValue.Auth;
            var cache = context.RequestServices.GetRequiredService<ApiKeyCache>();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("Blaze.LlmGateway.Api.Auth");

            var enforce = options.RequireApiKey
                ?? await cache.AnyKeysAsync(context.RequestAborted);

            if (!enforce)
            {
                // Open mode — optionally rate-limit by socket IP (never X-Forwarded-For).
                if (options.RequestsPerMinutePerIp > 0)
                {
                    var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                    if (!TryConsume($"ip:{ip}", options.RequestsPerMinutePerIp))
                    {
                        await WriteRateLimited(context, options.RequestsPerMinutePerIp);
                        return;
                    }
                }

                await next(context);
                return;
            }

            var presented = ExtractKey(context.Request);
            if (string.IsNullOrEmpty(presented))
            {
                await WriteUnauthorized(context, "Missing API key. Pass it as 'Authorization: Bearer <key>' or 'x-api-key: <key>'.");
                return;
            }

            var key = await cache.ValidateAsync(presented, context.RequestAborted);
            if (key is null)
            {
                logger.LogWarning("Rejected /v1 request with invalid API key from {RemoteIp}", context.Connection.RemoteIpAddress);
                await WriteUnauthorized(context, "Invalid API key.");
                return;
            }

            if (options.RequestsPerMinutePerKey > 0
                && !TryConsume($"key:{key.Id}", options.RequestsPerMinutePerKey))
            {
                await WriteRateLimited(context, options.RequestsPerMinutePerKey);
                return;
            }

            context.Items[ApiKeyItem] = key;
            await next(context);
        });
    }

    private static string? ExtractKey(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var token = authorization["Bearer ".Length..].Trim();
            if (token.Length > 0)
            {
                return token;
            }
        }

        var headerKey = request.Headers["x-api-key"].ToString();
        return string.IsNullOrWhiteSpace(headerKey) ? null : headerKey.Trim();
    }

    private static bool TryConsume(string bucketKey, int requestsPerMinute)
    {
        // rpm baked into the key so a config change gets a fresh bucket
        var bucket = Buckets.GetOrAdd($"{bucketKey}:{requestsPerMinute}", _ => new RateLimitBucket(requestsPerMinute, 0));
        return bucket.TryConsumeRequest();
    }

    private static Task WriteUnauthorized(HttpContext context, string message)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(new ErrorDetail(message, "invalid_request_error", "invalid_api_key")));
    }

    private static Task WriteRateLimited(HttpContext context, int requestsPerMinute)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(60.0 / requestsPerMinute));
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString();
        return context.Response.WriteAsJsonAsync(
            new ErrorResponse(new ErrorDetail(
                $"Rate limit exceeded. Retry after {retryAfterSeconds}s.",
                "rate_limit_error",
                "rate_limit_exceeded")));
    }
}
