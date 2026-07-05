using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.Infrastructure.Quota;

/// <summary>
/// P3.1: 9router's model-lock pattern. When a provider fails with a rate-limit/quota
/// signal it gets an exponential cooldown (2s·2^level, capped at 30 min). The router
/// skips locked providers at selection time instead of burning an attempt.
/// </summary>
public interface IModelLockRegistry
{
    /// <summary>True when the provider is cooling down; <paramref name="remaining"/> is time left.</summary>
    bool IsLocked(string providerKey, out TimeSpan remaining);

    /// <summary>Report a provider failure. Locks the provider only for rate-limit/quota signals.</summary>
    void ReportFailure(string providerKey, string failureMessage);

    /// <summary>Report success — clears the lock and resets the backoff level.</summary>
    void ReportSuccess(string providerKey);

    /// <summary>Current locks for the dashboard quota page.</summary>
    IReadOnlyList<ModelLockInfo> Snapshot();
}

public sealed record ModelLockInfo(string ProviderKey, DateTimeOffset LockedUntil, int BackoffLevel, string Reason);

public sealed class ModelLockRegistry(ILogger<ModelLockRegistry> logger) : IModelLockRegistry
{
    private static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(30);

    private static readonly string[] RateLimitSignals =
    [
        "429", "rate limit", "rate_limit", "too many requests", "quota", "capacity",
        "overloaded", "insufficient_quota", "resource exhausted", "resource_exhausted", "402"
    ];

    private sealed class LockState
    {
        public int Level;
        public DateTimeOffset Until;
        public string Reason = "";
    }

    private readonly ConcurrentDictionary<string, LockState> _locks = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocked(string providerKey, out TimeSpan remaining)
    {
        remaining = TimeSpan.Zero;
        if (!_locks.TryGetValue(providerKey, out var state))
        {
            return false;
        }

        var left = state.Until - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            return false;
        }

        remaining = left;
        return true;
    }

    public void ReportFailure(string providerKey, string failureMessage)
    {
        if (!IsRateLimitSignal(failureMessage))
        {
            return;
        }

        var state = _locks.GetOrAdd(providerKey, _ => new LockState());
        lock (state)
        {
            var cooldownTicks = Math.Min(
                BaseCooldown.Ticks * (1L << Math.Min(state.Level, 20)),
                MaxCooldown.Ticks);
            var cooldown = TimeSpan.FromTicks(cooldownTicks);
            state.Level++;
            state.Until = DateTimeOffset.UtcNow + cooldown;
            state.Reason = failureMessage.Length > 200 ? failureMessage[..200] : failureMessage;

            logger.LogWarning(
                "[MODEL-LOCK] {ProviderKey} locked for {Cooldown} (level {Level}): {Reason}",
                providerKey, cooldown, state.Level, state.Reason);
        }
    }

    public void ReportSuccess(string providerKey)
    {
        if (_locks.TryRemove(providerKey, out _))
        {
            logger.LogInformation("[MODEL-LOCK] {ProviderKey} lock cleared after success", providerKey);
        }
    }

    public IReadOnlyList<ModelLockInfo> Snapshot()
        => [.. _locks
            .Where(pair => pair.Value.Until > DateTimeOffset.UtcNow)
            .Select(pair => new ModelLockInfo(pair.Key, pair.Value.Until, pair.Value.Level, pair.Value.Reason))
            .OrderBy(info => info.LockedUntil)];

    /// <summary>Text-match rules checked the 9router way — before/independent of status codes.</summary>
    public static bool IsRateLimitSignal(string message)
        => RateLimitSignals.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));
}
