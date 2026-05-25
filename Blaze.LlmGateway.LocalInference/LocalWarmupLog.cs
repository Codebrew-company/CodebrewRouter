using Microsoft.Extensions.Logging;

namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Local Gemma startup warmup telemetry.
/// These tags are intentionally separate from request-routing [ROUTER-*] events.
/// </summary>
public static class LocalWarmupLog
{
    public const string StartTag = "[LOCAL-WARMUP-START]";
    public const string LoadTag = "[LOCAL-WARMUP-LOAD]";
    public const string PrimeTag = "[LOCAL-WARMUP-PRIME]";
    public const string ReadyTag = "[LOCAL-WARMUP-READY]";
    public const string SkipTag = "[LOCAL-WARMUP-SKIP]";
    public const string FailTag = "[LOCAL-WARMUP-FAIL]";

    public static void Start(ILogger logger, string? modelPath, bool blockStartupUntilWarm)
        => logger.LogInformation(
            "{Tag} Starting local Gemma warmup. ModelPath={ModelPath}, BlockStartupUntilWarm={BlockStartupUntilWarm}",
            StartTag,
            modelPath,
            blockStartupUntilWarm);

    public static void Load(ILogger logger, string? modelPath, bool loaded, long elapsedMilliseconds)
        => logger.LogInformation(
            "{Tag} Local Gemma model load state resolved. ModelPath={ModelPath}, Loaded={Loaded}, ElapsedMs={ElapsedMs}",
            LoadTag,
            modelPath,
            loaded,
            elapsedMilliseconds);

    public static void Prime(ILogger logger, string prompt, int maxOutputTokens)
        => logger.LogInformation(
            "{Tag} Priming local Gemma inference path. PromptLength={PromptLength}, MaxOutputTokens={MaxOutputTokens}",
            PrimeTag,
            prompt.Length,
            maxOutputTokens);

    public static void Ready(ILogger logger, string? modelPath, int chunks, long elapsedMilliseconds)
        => logger.LogInformation(
            "{Tag} Local Gemma warmup ready. ModelPath={ModelPath}, Chunks={Chunks}, ElapsedMs={ElapsedMs}",
            ReadyTag,
            modelPath,
            chunks,
            elapsedMilliseconds);

    public static void Skip(ILogger logger, string reason, string? modelPath = null)
        => logger.LogInformation(
            "{Tag} Local Gemma warmup skipped. Reason={Reason}, ModelPath={ModelPath}",
            SkipTag,
            reason,
            modelPath);

    public static void Fail(ILogger logger, string reason, string? modelPath, Exception? exception = null)
        => logger.LogWarning(
            exception,
            "{Tag} Local Gemma warmup failed. Reason={Reason}, ModelPath={ModelPath}",
            FailTag,
            reason,
            modelPath);
}
