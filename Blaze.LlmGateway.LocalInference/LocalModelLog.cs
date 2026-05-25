using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Log tags for provider-level model materialization (download, cache, resolve).
/// These are distinct from request-routing [ROUTER-*] and warmup [LOCAL-WARMUP-*] events.
/// </summary>
public static class LocalModelLog
{
    public const string ResolveTag = "[LOCAL-MODEL-RESOLVE]";
    public const string CacheHitTag = "[LOCAL-MODEL-CACHE-HIT]";
    public const string DownloadStartTag = "[LOCAL-MODEL-DOWNLOAD-START]";
    public const string DownloadReadyTag = "[LOCAL-MODEL-DOWNLOAD-READY]";
    public const string DownloadFailTag = "[LOCAL-MODEL-DOWNLOAD-FAIL]";

    public static void Resolve(ILogger logger, string? modelPath, string resolvedPath)
        => logger.LogInformation(
            "{Tag} Local model resolved from local path. ModelPath={ModelPath}, ResolvedPath={ResolvedPath}",
            ResolveTag,
            modelPath,
            resolvedPath);

    public static void CacheHit(ILogger logger, string modelUrl, string cachedPath)
        => logger.LogInformation(
            "{Tag} Local model cache hit, reusing cached file. ModelUrl={ModelUrl}, CachedPath={CachedPath}",
            CacheHitTag,
            modelUrl,
            cachedPath);

    public static void DownloadStart(ILogger logger, string modelUrl, string cacheDirectory)
        => logger.LogInformation(
            "{Tag} Starting model download. ModelUrl={ModelUrl}, CacheDirectory={CacheDirectory}",
            DownloadStartTag,
            modelUrl,
            cacheDirectory);

    public static void DownloadReady(ILogger logger, string modelUrl, string resolvedPath, long elapsedMilliseconds)
        => logger.LogInformation(
            "{Tag} Model download complete. ModelUrl={ModelUrl}, ResolvedPath={ResolvedPath}, ElapsedMs={ElapsedMs}",
            DownloadReadyTag,
            modelUrl,
            resolvedPath,
            elapsedMilliseconds);

    public static void DownloadFail(ILogger logger, string modelUrl, Exception? exception)
        => logger.LogError(
            exception,
            "{Tag} Model download failed. ModelUrl={ModelUrl}",
            DownloadFailTag,
            modelUrl);
}
