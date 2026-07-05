using System.Text;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Infrastructure.PromptCleaning;

/// <summary>
/// P4.2 RTK-style tool-output compression. Auto-detects a heuristic filter from the
/// content head and applies it. Fail-open by contract: any error, empty result, or
/// growth returns the original text unchanged.
/// Filters ported from 9router's highest-value set: gitDiff, buildOutput, dedupLog, smartTruncate.
/// </summary>
public static class ToolOutputCompressor
{
    public sealed record CompressionResult(string Text, string? Filter, int SavedChars);

    public static CompressionResult Compress(string text, ToolOutputCompressionOptions options)
    {
        if (string.IsNullOrEmpty(text) || text.Length < options.MinLengthChars)
        {
            return new CompressionResult(text, null, 0);
        }

        try
        {
            var filter = DetectFilter(text);
            var compressed = filter switch
            {
                "gitDiff" => CompressGitDiff(text),
                "buildOutput" => CompressBuildOutput(text),
                "dedupLog" => DedupLog(text),
                _ => SmartTruncate(text, options.MaxLengthChars)
            };

            // Fail-open: never return something bigger or empty.
            if (string.IsNullOrWhiteSpace(compressed) || compressed.Length >= text.Length)
            {
                // A structure-specific filter that failed to shrink still gets a truncation pass.
                compressed = SmartTruncate(text, options.MaxLengthChars);
                if (string.IsNullOrWhiteSpace(compressed) || compressed.Length >= text.Length)
                {
                    return new CompressionResult(text, null, 0);
                }

                filter = "smartTruncate";
            }

            return new CompressionResult(compressed, filter, text.Length - compressed.Length);
        }
        catch
        {
            return new CompressionResult(text, null, 0);
        }
    }

    /// <summary>Detects the best filter from the first 1KB of content.</summary>
    public static string DetectFilter(string text)
    {
        var head = text.Length > 1024 ? text[..1024] : text;

        if (head.Contains("diff --git", StringComparison.Ordinal)
            || (head.Contains("\n@@ ", StringComparison.Ordinal) && head.Contains("\n+++ ", StringComparison.Ordinal))
            || head.StartsWith("@@ ", StringComparison.Ordinal))
        {
            return "gitDiff";
        }

        if (head.Contains("error CS", StringComparison.Ordinal)
            || head.Contains("warning CS", StringComparison.Ordinal)
            || head.Contains("error TS", StringComparison.Ordinal)
            || head.Contains("npm ERR", StringComparison.Ordinal)
            || head.Contains("Build succeeded", StringComparison.Ordinal)
            || head.Contains("Build FAILED", StringComparison.Ordinal)
            || head.Contains("MSBuild version", StringComparison.Ordinal)
            || head.Contains("Determining projects to restore", StringComparison.Ordinal))
        {
            return "buildOutput";
        }

        if (HasHeavyDuplication(text))
        {
            return "dedupLog";
        }

        return "smartTruncate";
    }

    /// <summary>Keeps diff structure (headers, hunks, +/- lines); drops unchanged context lines.</summary>
    public static string CompressGitDiff(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length / 2);
        var droppedContext = 0;

        foreach (var line in lines)
        {
            var keep = line.StartsWith("diff --git", StringComparison.Ordinal)
                || line.StartsWith("index ", StringComparison.Ordinal)
                || line.StartsWith("--- ", StringComparison.Ordinal)
                || line.StartsWith("+++ ", StringComparison.Ordinal)
                || line.StartsWith("@@", StringComparison.Ordinal)
                || line.StartsWith('+')
                || line.StartsWith('-')
                || line.StartsWith("new file", StringComparison.Ordinal)
                || line.StartsWith("deleted file", StringComparison.Ordinal)
                || line.StartsWith("rename ", StringComparison.Ordinal)
                || line.StartsWith("Binary files", StringComparison.Ordinal);

            if (keep)
            {
                if (droppedContext > 0)
                {
                    builder.Append("  [").Append(droppedContext).Append(" unchanged lines]\n");
                    droppedContext = 0;
                }

                builder.Append(line).Append('\n');
            }
            else
            {
                droppedContext++;
            }
        }

        if (droppedContext > 0)
        {
            builder.Append("  [").Append(droppedContext).Append(" unchanged lines]\n");
        }

        return builder.ToString();
    }

    /// <summary>Keeps errors, warnings, failures, and the summary tail; drops routine build noise.</summary>
    public static string CompressBuildOutput(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length / 3);
        var dropped = 0;

        // Signal lines anywhere + the final summary block.
        var tailStart = Math.Max(0, lines.Length - 12);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var isSignal = line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                || line.Contains("FAILED", StringComparison.Ordinal)
                || line.Contains("failed", StringComparison.Ordinal)
                || line.Contains("exception", StringComparison.OrdinalIgnoreCase)
                || i >= tailStart;

            if (isSignal)
            {
                if (dropped > 0)
                {
                    builder.Append("  [").Append(dropped).Append(" build-noise lines]\n");
                    dropped = 0;
                }

                builder.Append(line).Append('\n');
            }
            else
            {
                dropped++;
            }
        }

        if (dropped > 0)
        {
            builder.Append("  [").Append(dropped).Append(" build-noise lines]\n");
        }

        return builder.ToString();
    }

    /// <summary>Collapses consecutive duplicate lines into "line  [×N]".</summary>
    public static string DedupLog(string text)
    {
        var lines = text.Split('\n');
        var builder = new StringBuilder(text.Length / 2);
        string? previous = null;
        var repeat = 0;

        foreach (var line in lines)
        {
            if (line == previous)
            {
                repeat++;
                continue;
            }

            FlushRepeat(builder, repeat);
            repeat = 0;
            builder.Append(line).Append('\n');
            previous = line;
        }

        FlushRepeat(builder, repeat);
        return builder.ToString();

        static void FlushRepeat(StringBuilder builder, int repeat)
        {
            if (repeat > 0)
            {
                builder.Append("  [previous line repeated ").Append(repeat).Append(" more times]\n");
            }
        }
    }

    /// <summary>Head + tail with an elision marker, capped at maxChars.</summary>
    public static string SmartTruncate(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        var headLength = (int)(maxChars * 0.6);
        var tailLength = maxChars - headLength;
        return text[..headLength]
            + $"\n… [{text.Length - maxChars} chars elided] …\n"
            + text[^tailLength..];
    }

    private static bool HasHeavyDuplication(string text)
    {
        var lines = text.Split('\n');
        if (lines.Length < 20)
        {
            return false;
        }

        var consecutiveDupes = 0;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length > 0 && lines[i] == lines[i - 1])
            {
                consecutiveDupes++;
            }
        }

        return consecutiveDupes > lines.Length / 5;
    }
}
