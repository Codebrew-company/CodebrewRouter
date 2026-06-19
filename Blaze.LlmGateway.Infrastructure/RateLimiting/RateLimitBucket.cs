using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Blaze.LlmGateway.Infrastructure.RateLimiting;

/// <summary>
/// Token-bucket rate limiter that tracks request count and token count per
/// deployment. Uses a sliding window with configurable per-minute limits.
/// Thread-safe — all operations are non-blocking.
/// </summary>
public sealed class RateLimitBucket
{
    private readonly int _maxRequestsPerMinute;
    private readonly int _maxTokensPerMinute;
    private readonly double _requestRefillRate;
    private readonly double _tokenRefillRate;

    private double _requestTokens;
    private double _tokenTokens;
    private long _lastRefillTimestamp;
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new rate-limit bucket.
    /// </summary>
    /// <param name="maxRequestsPerMinute">Max requests per minute. 0 = unlimited.</param>
    /// <param name="maxTokensPerMinute">Max output tokens per minute. 0 = unlimited.</param>
    public RateLimitBucket(int maxRequestsPerMinute, int maxTokensPerMinute)
    {
        _maxRequestsPerMinute = maxRequestsPerMinute;
        _maxTokensPerMinute = maxTokensPerMinute;

        _requestRefillRate = maxRequestsPerMinute > 0 ? (double)maxRequestsPerMinute / 60_000.0 : 0;
        _tokenRefillRate = maxTokensPerMinute > 0 ? (double)maxTokensPerMinute / 60_000.0 : 0;

        _requestTokens = maxRequestsPerMinute > 0 ? maxRequestsPerMinute : double.MaxValue;
        _tokenTokens = maxTokensPerMinute > 0 ? maxTokensPerMinute : double.MaxValue;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Attempts to consume one request from the bucket.
    /// Returns true if allowed, false if rate-limited.
    /// </summary>
    public bool TryConsumeRequest()
    {
        if (_maxRequestsPerMinute <= 0)
            return true;

        lock (_lock)
        {
            Refill();
            if (_requestTokens >= 1.0)
            {
                _requestTokens -= 1.0;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Attempts to reserve <paramref name="tokenCount"/> tokens.
    /// Returns the number of tokens actually reserved (may be less than requested).
    /// Returns 0 if rate-limited.
    /// </summary>
    public int TryReserveTokens(int tokenCount)
    {
        if (_maxTokensPerMinute <= 0 || tokenCount <= 0)
            return tokenCount;

        lock (_lock)
        {
            Refill();
            var available = Math.Min((int)Math.Floor(_tokenTokens), tokenCount);
            if (available > 0)
            {
                _tokenTokens -= available;
            }
            return available;
        }
    }

    /// <summary>
    /// Returns available request tokens (for diagnostics).
    /// </summary>
    public double AvailableRequests
    {
        get
        {
            lock (_lock) { Refill(); return _requestTokens; }
        }
    }

    /// <summary>
    /// Returns available token tokens (for diagnostics).
    /// </summary>
    public double AvailableTokens
    {
        get
        {
            lock (_lock) { Refill(); return _tokenTokens; }
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = (now - _lastRefillTimestamp) / (double)Stopwatch.Frequency * 1000.0; // ms

        if (elapsed <= 0)
            return;

        _lastRefillTimestamp = now;

        if (_maxRequestsPerMinute > 0)
        {
            _requestTokens = Math.Min(_maxRequestsPerMinute, _requestTokens + elapsed * _requestRefillRate);
        }

        if (_maxTokensPerMinute > 0)
        {
            _tokenTokens = Math.Min(_maxTokensPerMinute, _tokenTokens + elapsed * _tokenRefillRate);
        }
    }
}
