using System.Text;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.PromptCleaning;
using FluentAssertions;
using Xunit;

namespace Blaze.LlmGateway.Tests.PromptCleaning;

/// <summary>P4.2: RTK-style tool-output compression filters (fail-open contract).</summary>
public sealed class ToolOutputCompressorTests
{
    private static readonly ToolOutputCompressionOptions Options = new()
    {
        Enabled = true,
        MinLengthChars = 100,
        MaxLengthChars = 5000
    };

    [Fact]
    public void ShortContent_IsNeverTouched()
    {
        var result = ToolOutputCompressor.Compress("short tool output", Options);

        result.Filter.Should().BeNull();
        result.SavedChars.Should().Be(0);
    }

    [Fact]
    public void GitDiff_DropsContextLines_KeepsChanges()
    {
        var diff = new StringBuilder();
        diff.AppendLine("diff --git a/File.cs b/File.cs");
        diff.AppendLine("index 123..456 100644");
        diff.AppendLine("--- a/File.cs");
        diff.AppendLine("+++ b/File.cs");
        diff.AppendLine("@@ -1,50 +1,50 @@");
        for (var i = 0; i < 40; i++)
        {
            diff.AppendLine($" unchanged context line {i}");
        }

        diff.AppendLine("-old line");
        diff.AppendLine("+new line");

        var result = ToolOutputCompressor.Compress(diff.ToString(), Options);

        result.Filter.Should().Be("gitDiff");
        result.SavedChars.Should().BeGreaterThan(0);
        result.Text.Should().Contain("+new line");
        result.Text.Should().Contain("-old line");
        result.Text.Should().Contain("@@ -1,50 +1,50 @@");
        result.Text.Should().NotContain("unchanged context line 5");
        result.Text.Should().Contain("[40 unchanged lines]");
    }

    [Fact]
    public void BuildOutput_KeepsErrorsAndSummary_CompressesFiftyKbLogByFortyPercent()
    {
        var log = new StringBuilder();
        log.AppendLine("MSBuild version 17.0 for .NET");
        log.AppendLine("Determining projects to restore...");
        while (log.Length < 50_000)
        {
            log.AppendLine("  Compiling Blaze.LlmGateway.Infrastructure -> obj/Debug/net10.0/file.dll copying artifacts and analyzers");
        }

        log.AppendLine("E:\\src\\File.cs(10,5): error CS1002: ; expected");
        log.AppendLine("Build FAILED.");
        log.AppendLine("    1 Error(s)");

        var result = ToolOutputCompressor.Compress(log.ToString(), Options);

        result.Filter.Should().Be("buildOutput");
        result.Text.Should().Contain("error CS1002");
        result.Text.Should().Contain("Build FAILED.");
        // Acceptance: synthetic 50KB build log compresses ≥40%.
        result.SavedChars.Should().BeGreaterThan((int)(log.Length * 0.4));
    }

    [Fact]
    public void DedupLog_CollapsesConsecutiveDuplicates()
    {
        var log = new StringBuilder();
        for (var i = 0; i < 30; i++)
        {
            log.AppendLine("Retrying connection to upstream provider...");
        }

        log.AppendLine("Connected.");

        var result = ToolOutputCompressor.Compress(log.ToString(), Options);

        result.Filter.Should().Be("dedupLog");
        result.Text.Should().Contain("repeated 29 more times");
        result.Text.Should().Contain("Connected.");
    }

    [Fact]
    public void SmartTruncate_KeepsHeadAndTail()
    {
        var text = new string('a', 4000) + "MIDDLE" + new string('z', 4000);
        var result = ToolOutputCompressor.Compress(text, new ToolOutputCompressionOptions
        {
            Enabled = true,
            MinLengthChars = 100,
            MaxLengthChars = 1000
        });

        result.Filter.Should().Be("smartTruncate");
        result.Text.Should().StartWith("aaa");
        result.Text.Should().EndWith("zzz");
        result.Text.Should().Contain("chars elided");
        result.Text.Length.Should().BeLessThan(1100);
    }

    [Fact]
    public void FailedFilter_PassesOriginalThrough()
    {
        // A "diff" that is all +/- lines cannot shrink — fail-open returns original.
        var diff = "diff --git a/x b/x\n" + string.Join('\n', Enumerable.Range(0, 50).Select(i => $"+line {i}"));

        var result = ToolOutputCompressor.Compress(diff, Options);

        result.Text.Length.Should().BeLessThanOrEqualTo(diff.Length);
        if (result.SavedChars == 0)
        {
            result.Text.Should().Be(diff);
        }
    }
}
