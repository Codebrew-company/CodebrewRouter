# Context-Aware Routing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When a prompt exceeds a provider's context window, compact the history to fit; if still too large, re-route to a provider with a bigger window; return HTTP 413 only when no provider can serve.

**Architecture:** A new `ContextSizingChatClient` (DelegatingChatClient) wraps each keyed provider. It counts tokens on every request, compacts via `IContextCompactor` when over budget, and throws `ContextOverflowException` when even compaction is insufficient. `LlmRoutingChatClient` catches `ContextOverflowException` and filters its failover chain to providers whose `MaxContextTokens` (already in provider options) can fit the payload; if the chain is exhausted, the exception propagates. `ChatCompletionsEndpoint` maps an unhandled `ContextOverflowException` to HTTP 413.

**Tech Stack:** .NET 10, Microsoft.Extensions.AI `DelegatingChatClient` / `ChatClientBuilder`, xUnit 2.x, Moq 4.x, FluentAssertions (add via NuGet), existing `ITokenCounter` / `IContextCompactor`.

---

## File Map

| Action | File | Purpose |
|--------|------|---------|
| Create | `Blaze.LlmGateway.Core/Configuration/ContextSizingOptions.cs` | Config POCO for context sizing |
| Modify | `Blaze.LlmGateway.Core/Configuration/LlmGatewayOptions.cs` | Add `ContextSizing` property |
| Create | `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextOverflowException.cs` | Exception thrown when prompt can't fit |
| Create | `Blaze.LlmGateway.Infrastructure/ModelCatalog/ModelContextLimits.cs` | Curated context-window lookup table |
| Create | `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClient.cs` | Token-count → compact → throw middleware |
| Create | `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClientBuilderExtensions.cs` | `.UseContextSizing()` builder extension |
| Modify | `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs` | Chain `.UseContextSizing()` on every keyed provider; register `ContextSizingOptions` |
| Modify | `Blaze.LlmGateway.Infrastructure/ModelSelectionResolver.cs` | Chain `.UseContextSizing()` on dynamic Ollama clients |
| Modify | `Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs` | Catch `ContextOverflowException`; add `CanFit()`; size-aware failover |
| Modify | `Blaze.LlmGateway.Api/ChatCompletionsEndpoint.cs` | Map `ContextOverflowException` → HTTP 413 |
| Create | `Blaze.LlmGateway.Tests/ModelContextLimitsTests.cs` | Unit tests for curated table |
| Create | `Blaze.LlmGateway.Tests/ContextSizingChatClientTests.cs` | Unit tests for sizing middleware |
| Modify | `Blaze.LlmGateway.Tests/LlmRoutingChatClientTests.cs` | Unit tests for overflow routing |

---

## Task 1: Add `ContextSizingOptions` and wire into `LlmGatewayOptions`

**Files:**
- Create: `Blaze.LlmGateway.Core/Configuration/ContextSizingOptions.cs`
- Modify: `Blaze.LlmGateway.Core/Configuration/LlmGatewayOptions.cs:1-12`

- [ ] **Step 1: Create `ContextSizingOptions`**

```csharp
// Blaze.LlmGateway.Core/Configuration/ContextSizingOptions.cs
namespace Blaze.LlmGateway.Core.Configuration;

/// <summary>
/// Controls per-provider context window enforcement.
/// Binds from <c>LlmGateway:ContextSizing</c>.
/// </summary>
public class ContextSizingOptions
{
    /// <summary>When false, no token counting or compaction is attempted.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Tokens reserved for the model's output when the caller does not specify
    /// <c>ChatOptions.MaxOutputTokens</c>.
    /// </summary>
    public int DefaultReservedOutputTokens { get; set; } = 1024;
}
```

- [ ] **Step 2: Add `ContextSizing` property to `LlmGatewayOptions`**

In `Blaze.LlmGateway.Core/Configuration/LlmGatewayOptions.cs`, add after line 11 (`PromptCleanup`):

```csharp
    public ContextSizingOptions ContextSizing { get; set; } = new();
```

Full file after the change (lines 1–13):

```csharp
namespace Blaze.LlmGateway.Core.Configuration;

public class LlmGatewayOptions
{
    public const string SectionName = "LlmGateway";

    public ProvidersOptions Providers { get; set; } = new();
    public RoutingOptions Routing { get; set; } = new();
    public CodebrewRouterOptions CodebrewRouter { get; set; } = new();
    public ModelAvailabilityOptions Availability { get; set; } = new();
    public PromptCleanupOptions PromptCleanup { get; set; } = new();
    public ContextSizingOptions ContextSizing { get; set; } = new();
}
```

- [ ] **Step 3: Build and fix any errors**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

Expected: build succeeds with 0 errors, 0 warnings.

- [ ] **Step 4: Commit**

```
git add Blaze.LlmGateway.Core/Configuration/ContextSizingOptions.cs
git add Blaze.LlmGateway.Core/Configuration/LlmGatewayOptions.cs
git commit -m "feat(config): add ContextSizingOptions to LlmGatewayOptions"
```

---

## Task 2: Add `ContextOverflowException`

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextOverflowException.cs`

- [ ] **Step 1: Create the exception class**

```csharp
// Blaze.LlmGateway.Infrastructure/ContextHandling/ContextOverflowException.cs
namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

/// <summary>
/// Thrown when a prompt cannot fit in the target model's context window even after compaction.
/// <see cref="LlmRoutingChatClient"/> catches this and attempts to route to a provider with a
/// larger window. If no provider fits, the exception propagates so the API layer can return 413.
/// </summary>
public sealed class ContextOverflowException : Exception
{
    /// <summary>Model ID that was attempted.</summary>
    public string ModelId { get; }

    /// <summary>Token count that could not be reduced further.</summary>
    public int RequiredTokens { get; }

    /// <summary>Input budget (context window minus reserved output tokens) for <see cref="ModelId"/>.</summary>
    public int Budget { get; }

    /// <summary>Provider keys that have already been tried and rejected.</summary>
    public IReadOnlyList<string> AttemptedDestinations { get; }

    public ContextOverflowException(
        string modelId,
        int requiredTokens,
        int budget,
        IReadOnlyList<string> attemptedDestinations)
        : base($"Context overflow for model '{modelId}': {requiredTokens} tokens required but budget is {budget}.")
    {
        ModelId = modelId;
        RequiredTokens = requiredTokens;
        Budget = budget;
        AttemptedDestinations = attemptedDestinations;
    }

    /// <summary>
    /// Returns a new exception with <paramref name="destination"/> appended to
    /// <see cref="AttemptedDestinations"/>.  Used by the failover loop to track
    /// which providers have been tried.
    /// </summary>
    public ContextOverflowException WithAttempted(string destination) =>
        new(ModelId, RequiredTokens, Budget, [.. AttemptedDestinations, destination]);
}
```

- [ ] **Step 2: Build**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Commit**

```
git add Blaze.LlmGateway.Infrastructure/ContextHandling/ContextOverflowException.cs
git commit -m "feat(context): add ContextOverflowException with WithAttempted builder"
```

---

## Task 3: Add `ModelContextLimits` curated table + unit tests

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/ModelCatalog/ModelContextLimits.cs`
- Create: `Blaze.LlmGateway.Tests/ModelContextLimitsTests.cs`

The table is used when `ModelSelectionResolver` builds a dynamic Ollama client—it has no provider options to read, so it falls back here.

- [ ] **Step 1: Write failing tests first**

```csharp
// Blaze.LlmGateway.Tests/ModelContextLimitsTests.cs
using Blaze.LlmGateway.Infrastructure.ModelCatalog;
using FluentAssertions;

namespace Blaze.LlmGateway.Tests;

public class ModelContextLimitsTests
{
    [Theory]
    [InlineData("gpt-4o",              128_000, 16_384)]
    [InlineData("gpt-4o-mini",         128_000, 16_384)]
    [InlineData("gpt-4o-2024-08-06",   128_000, 16_384)]  // versioned gpt-4o
    [InlineData("gemini-2.0-flash",    1_048_576, 8_192)]
    [InlineData("llama3.1:8b",         128_000, 4_096)]
    [InlineData("llama3.2:3b",         128_000, 4_096)]
    [InlineData("phi-4",               16_384,  4_096)]
    [InlineData("phi-4-mini",          16_384,  4_096)]
    [InlineData("qwen2.5:7b",          128_000, 4_096)]
    public void Lookup_KnownModel_ReturnsExpectedLimits(
        string modelId, int expectedContext, int expectedMaxOut)
    {
        var (ctx, maxOut) = ModelContextLimits.Lookup(modelId);
        ctx.Should().Be(expectedContext);
        maxOut.Should().Be(expectedMaxOut);
    }

    [Theory]
    [InlineData("unknown-model-xyz")]
    [InlineData("")]
    public void Lookup_UnknownModel_ReturnsNulls(string modelId)
    {
        var (ctx, maxOut) = ModelContextLimits.Lookup(modelId);
        ctx.Should().BeNull();
        maxOut.Should().BeNull();
    }
}
```

- [ ] **Step 2: Add FluentAssertions to test project (if not already present)**

```
dotnet add Blaze.LlmGateway.Tests/Blaze.LlmGateway.Tests.csproj package FluentAssertions
```

- [ ] **Step 3: Run tests to confirm they fail**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~ModelContextLimitsTests" --no-build
```

Expected: compiler error — `ModelContextLimits` does not exist yet.

- [ ] **Step 4: Create `ModelContextLimits`**

```csharp
// Blaze.LlmGateway.Infrastructure/ModelCatalog/ModelContextLimits.cs
namespace Blaze.LlmGateway.Infrastructure.ModelCatalog;

/// <summary>
/// Curated context-window limits for well-known models.
/// Used as a fallback when a provider's config does not specify limits
/// (e.g. dynamically resolved Ollama models).
/// Most-specific entries must come before less-specific ones.
/// </summary>
public static class ModelContextLimits
{
    // (predicate, contextWindow, maxOutput)
    private static readonly (Func<string, bool> Match, int ContextWindow, int MaxOutput)[] Table =
    [
        (id => id.StartsWith("gpt-4o-mini",          StringComparison.OrdinalIgnoreCase), 128_000,   16_384),
        (id => id.StartsWith("gpt-4o",               StringComparison.OrdinalIgnoreCase), 128_000,   16_384),
        (id => id.Contains("gemini-2.0-flash",        StringComparison.OrdinalIgnoreCase), 1_048_576, 8_192),
        (id => id.StartsWith("llama3.1:",             StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
        (id => id.StartsWith("llama3.2:",             StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
        (id => id.StartsWith("phi-4",                 StringComparison.OrdinalIgnoreCase), 16_384,    4_096),
        (id => id.StartsWith("qwen2.5:",              StringComparison.OrdinalIgnoreCase), 128_000,   4_096),
    ];

    /// <summary>
    /// Returns <c>(ContextWindow, MaxOutput)</c> for <paramref name="modelId"/>,
    /// or <c>(null, null)</c> if the model is not in the curated table.
    /// </summary>
    public static (int? ContextWindow, int? MaxOutput) Lookup(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return (null, null);

        foreach (var (match, ctx, maxOut) in Table)
        {
            if (match(modelId))
                return (ctx, maxOut);
        }

        return (null, null);
    }
}
```

- [ ] **Step 5: Run tests to confirm they pass**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~ModelContextLimitsTests" -v normal
```

Expected: all 11 tests pass.

- [ ] **Step 6: Build clean**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 7: Commit**

```
git add Blaze.LlmGateway.Infrastructure/ModelCatalog/ModelContextLimits.cs
git add Blaze.LlmGateway.Tests/ModelContextLimitsTests.cs
git commit -m "feat(catalog): add ModelContextLimits curated table with unit tests"
```

---

## Task 4: Implement `ContextSizingChatClient` + unit tests

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClient.cs`
- Create: `Blaze.LlmGateway.Tests/ContextSizingChatClientTests.cs`

### Design

`ContextSizingChatClient` wraps an `IChatClient` and adds a pre-flight before every call:

1. Count tokens with `ITokenCounter`.
2. If `≤ budget` → pass through unchanged.
3. If `> budget` → call `IContextCompactor.CompactAsync(messages, budget, modelId, ct)`.
4. If compacted result fits → forward compacted messages.
5. If still over budget → throw `ContextOverflowException(modelId, compactedCount, budget, [])`.

`budget = contextWindowTokens - (options?.MaxOutputTokens ?? reservedOutputTokens)`.

The streaming override performs the same pre-flight synchronously (before the first `yield`), so `TryGetFirstChunkAsync` can detect overflow through the normal MoveNextAsync exception path.

- [ ] **Step 1: Write failing tests**

```csharp
// Blaze.LlmGateway.Tests/ContextSizingChatClientTests.cs
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Tests;

public class ContextSizingChatClientTests
{
    private static IOptions<ContextSizingOptions> EnabledOptions(int reservedOutput = 256) =>
        Options.Create(new ContextSizingOptions { Enabled = true, DefaultReservedOutputTokens = reservedOutput });

    private static IOptions<ContextSizingOptions> DisabledOptions() =>
        Options.Create(new ContextSizingOptions { Enabled = false });

    private static List<ChatMessage> OneUserMessage(string text = "hello") =>
        [new ChatMessage(ChatRole.User, text)];

    // ──────────────────────────────────────────────────────────────
    // Pass-through: tokens fit within budget
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_TokensFit_ForwardsToInnerClient()
    {
        var messages = OneUserMessage();
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(messages, It.IsAny<string?>())).Returns(100);

        var compactorMock = new Mock<IContextCompactor>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        compactorMock.Verify(c => c.CompactAsync(
            It.IsAny<IList<ChatMessage>>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ──────────────────────────────────────────────────────────────
    // Compaction succeeds: over budget, compact, fits, forward compacted
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_OverBudget_CompactsAndForwards()
    {
        var original = OneUserMessage("very long message");
        var compacted = OneUserMessage("summary");
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(compacted, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(original, It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(original, 744 /* 1000-256 */, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(compacted, 900, 200, true, "summarize"));

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(original, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        innerMock.Verify(c => c.GetResponseAsync(compacted, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────
    // Compaction insufficient: over budget after compaction → throw
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_CompactionInsufficient_ThrowsContextOverflowException()
    {
        var messages = OneUserMessage("extremely long");

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(messages, It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(messages, 744, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 900, 800, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var act = () => sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        await act.Should().ThrowAsync<ContextOverflowException>()
            .Where(ex => ex.ModelId == "test-model"
                      && ex.RequiredTokens == 800
                      && ex.Budget == 744
                      && ex.AttemptedDestinations.Count == 0);

        innerMock.Verify(c => c.GetResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ──────────────────────────────────────────────────────────────
    // Disabled: no token counting, no compaction
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_Disabled_SkipsCountingAndForwards()
    {
        var messages = OneUserMessage();
        var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "ok")]);

        var innerMock = new Mock<IChatClient>();
        innerMock
            .Setup(c => c.GetResponseAsync(messages, It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);

        var tokenCounterMock = new Mock<ITokenCounter>();
        var compactorMock = new Mock<IContextCompactor>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            DisabledOptions(),
            contextWindowTokens: 10,   // tiny window — would overflow if enabled
            reservedOutputTokens: 5,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var result = await sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

        result.Should().BeSameAs(expectedResponse);
        tokenCounterMock.Verify(t => t.CountTokens(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<string?>()), Times.Never);
    }

    // ──────────────────────────────────────────────────────────────
    // MaxOutputTokens from ChatOptions overrides default reserved
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetResponseAsync_ChatOptionsMaxOutputTokensOverridesDefault()
    {
        var messages = OneUserMessage();

        // window=1000, caller requests MaxOutputTokens=500 → budget=500
        // token count = 600 → over budget even though it would fit with default reserved=256 (budget=744)
        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(messages, It.IsAny<string?>())).Returns(600);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(messages, 500, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 600, 600, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var options = new ChatOptions { MaxOutputTokens = 500 };
        var act = () => sut.GetResponseAsync(messages, options, CancellationToken.None);

        await act.Should().ThrowAsync<ContextOverflowException>()
            .Where(ex => ex.Budget == 500);
    }

    // ──────────────────────────────────────────────────────────────
    // Streaming: overflow thrown before first chunk
    // ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStreamingResponseAsync_CompactionInsufficient_ThrowsBeforeFirstChunk()
    {
        var messages = OneUserMessage("huge");

        var tokenCounterMock = new Mock<ITokenCounter>();
        tokenCounterMock.Setup(t => t.CountTokens(messages, It.IsAny<string?>())).Returns(900);

        var compactorMock = new Mock<IContextCompactor>();
        compactorMock
            .Setup(c => c.CompactAsync(messages, 744, "test-model", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContextCompactionResult(messages, 900, 800, false, "none"));

        var innerMock = new Mock<IChatClient>();

        var sut = new ContextSizingChatClient(
            innerMock.Object,
            tokenCounterMock.Object,
            compactorMock.Object,
            EnabledOptions(reservedOutput: 256),
            contextWindowTokens: 1000,
            reservedOutputTokens: 256,
            modelId: "test-model",
            NullLogger<ContextSizingChatClient>.Instance);

        var enumerator = sut.GetStreamingResponseAsync(messages, new ChatOptions(), CancellationToken.None)
                            .GetAsyncEnumerator();

        // The ContextOverflowException is thrown on the first MoveNextAsync call.
        var act = () => enumerator.MoveNextAsync().AsTask();

        await act.Should().ThrowAsync<ContextOverflowException>();
        innerMock.Verify(c => c.GetStreamingResponseAsync(
            It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
```

- [ ] **Step 2: Run tests to confirm they fail (class not found)**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~ContextSizingChatClientTests" 2>&1 | Select-String -Pattern "error|Error|FAILED|failed"
```

Expected: compiler errors saying `ContextSizingChatClient` not found.

- [ ] **Step 3: Create `ContextSizingChatClient`**

```csharp
// Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClient.cs
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

/// <summary>
/// Middleware that enforces a context window on every request:
/// counts tokens, compacts when over budget, throws
/// <see cref="ContextOverflowException"/> when even compaction is insufficient.
/// </summary>
public sealed class ContextSizingChatClient : DelegatingChatClient
{
    private readonly ITokenCounter _tokenCounter;
    private readonly IContextCompactor _compactor;
    private readonly IOptions<ContextSizingOptions> _options;
    private readonly int _contextWindowTokens;
    private readonly int _reservedOutputTokens;
    private readonly string _modelId;
    private readonly ILogger<ContextSizingChatClient> _logger;

    public ContextSizingChatClient(
        IChatClient innerClient,
        ITokenCounter tokenCounter,
        IContextCompactor compactor,
        IOptions<ContextSizingOptions> options,
        int contextWindowTokens,
        int reservedOutputTokens,
        string modelId,
        ILogger<ContextSizingChatClient> logger) : base(innerClient)
    {
        _tokenCounter = tokenCounter;
        _compactor = compactor;
        _options = options;
        _contextWindowTokens = contextWindowTokens;
        _reservedOutputTokens = reservedOutputTokens;
        _modelId = modelId;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var fittedMessages = await EnsureFitsAsync(chatMessages, options, cancellationToken);
        return await InnerClient.GetResponseAsync(fittedMessages, options, cancellationToken);
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
        // Pre-flight: may throw ContextOverflowException BEFORE the first yield
        // so that TryGetFirstChunkAsync (in LlmRoutingChatClient) catches it via MoveNextAsync.
        var fittedMessages = await EnsureFitsAsync(chatMessages, options, cancellationToken);

        await foreach (var update in InnerClient.GetStreamingResponseAsync(fittedMessages, options, cancellationToken))
            yield return update;
    }

    // ──────────────────────────────────────────────────────────────
    // Core logic
    // ──────────────────────────────────────────────────────────────

    private async Task<IList<ChatMessage>> EnsureFitsAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var messages = chatMessages.ToList();

        if (!_options.Value.Enabled)
            return messages;

        var reserved = options?.MaxOutputTokens ?? _reservedOutputTokens;
        var budget = _contextWindowTokens - reserved;

        if (budget <= 0)
        {
            _logger.LogWarning(
                "Context window {Window} - reserved {Reserved} = non-positive budget for model {ModelId}; throwing overflow",
                _contextWindowTokens, reserved, _modelId);
            throw new ContextOverflowException(_modelId, int.MaxValue, budget, []);
        }

        var tokenCount = _tokenCounter.CountTokens(messages, _modelId);
        if (tokenCount <= budget)
        {
            _logger.LogDebug(
                "Prompt fits: {Tokens}/{Budget} tokens for model {ModelId}",
                tokenCount, budget, _modelId);
            return messages;
        }

        _logger.LogInformation(
            "Prompt ({Tokens} tokens) exceeds budget ({Budget}) for model {ModelId}; compacting",
            tokenCount, budget, _modelId);

        var compactionResult = await _compactor.CompactAsync(messages, budget, _modelId, cancellationToken);

        if (compactionResult.CompactedTokenCount <= budget)
        {
            _logger.LogInformation(
                "Compaction succeeded for model {ModelId}: {Original}→{Compacted} tokens",
                _modelId, compactionResult.OriginalTokenCount, compactionResult.CompactedTokenCount);
            return compactionResult.Messages.ToList();
        }

        _logger.LogWarning(
            "Compaction insufficient for model {ModelId}: {Compacted} tokens still > budget {Budget}",
            _modelId, compactionResult.CompactedTokenCount, budget);

        throw new ContextOverflowException(
            _modelId,
            compactionResult.CompactedTokenCount,
            budget,
            []);
    }
}
```

- [ ] **Step 4: Run tests**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~ContextSizingChatClientTests" -v normal
```

Expected: all 6 tests pass.

- [ ] **Step 5: Build clean**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 6: Commit**

```
git add Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClient.cs
git add Blaze.LlmGateway.Tests/ContextSizingChatClientTests.cs
git commit -m "feat(context): implement ContextSizingChatClient with TDD"
```

---

## Task 5: Add `UseContextSizing()` builder extension

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClientBuilderExtensions.cs`

- [ ] **Step 1: Create the extension**

```csharp
// Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClientBuilderExtensions.cs
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure.ContextHandling;

public static class ContextSizingChatClientBuilderExtensions
{
    /// <summary>
    /// Inserts <see cref="ContextSizingChatClient"/> into the chat-client pipeline.
    /// Call this inside a <c>AddKeyedSingleton&lt;IChatClient&gt;</c> factory after
    /// <c>.AsBuilder()</c>, before <c>.Build()</c>.
    /// </summary>
    public static ChatClientBuilder UseContextSizing(
        this ChatClientBuilder builder,
        ITokenCounter tokenCounter,
        IContextCompactor compactor,
        IOptions<ContextSizingOptions> sizingOptions,
        int contextWindowTokens,
        int reservedOutputTokens,
        string modelId,
        ILogger<ContextSizingChatClient> logger)
        => builder.Use(inner => new ContextSizingChatClient(
               inner,
               tokenCounter,
               compactor,
               sizingOptions,
               contextWindowTokens,
               reservedOutputTokens,
               modelId,
               logger));
}
```

- [ ] **Step 2: Build**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 3: Commit**

```
git add Blaze.LlmGateway.Infrastructure/ContextHandling/ContextSizingChatClientBuilderExtensions.cs
git commit -m "feat(context): add UseContextSizing ChatClientBuilder extension"
```

---

## Task 6: Wire `ContextSizingChatClient` into keyed provider registrations

**Files:**
- Modify: `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs`

Each keyed provider factory already has access to `sp` (the `IServiceProvider`). We resolve sizing dependencies from `sp` and chain `.UseContextSizing()` before `.Build()`.

The existing provider options already carry `MaxContextTokens` and `ReservedOutputTokens` — use them directly. No new config keys required.

Also register `IOptions<ContextSizingOptions>` from the root options, mirroring the existing pattern for `ContextCompactionOptions`.

- [ ] **Step 1: Add `ContextSizingOptions` DI registration**

In `InfrastructureServiceExtensions.cs`, inside `AddLlmInfrastructure()`, after the `ContextCompactionOptions` registration (line 159), add:

```csharp
        services.AddSingleton<IOptions<ContextSizingOptions>>(sp =>
            Options.Create(sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.ContextSizing));
```

- [ ] **Step 2: Add required using**

At the top of `InfrastructureServiceExtensions.cs`, the `ContextSizingOptions` namespace is already included via `Blaze.LlmGateway.Core.Configuration`. Add the missing using for the extensions:

```csharp
using Blaze.LlmGateway.Infrastructure.ContextHandling;
```

- [ ] **Step 3: Wire AzureFoundry**

Replace the AzureFoundry factory body. The full new factory (replace lines 27–44):

```csharp
        services.AddKeyedSingleton<IChatClient>("AzureFoundry", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.AzureFoundry;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextSizingChatClient>>();

            if (!string.IsNullOrWhiteSpace(opts.ResponsesEndpoint))
            {
                return new FoundryResponsesChatClient(
                        opts,
                        sp.GetRequiredService<ILogger<FoundryResponsesChatClient>>())
                    .AsBuilder()
                    .UseFunctionInvocation()
                    .UseContextSizing(tokenCounter, compactor, sizingOptions,
                        opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                    .Build();
            }

            AzureOpenAIClient azureClient = string.IsNullOrWhiteSpace(opts.ApiKey)
                ? new AzureOpenAIClient(new Uri(opts.Endpoint), new DefaultAzureCredential())
                : new AzureOpenAIClient(new Uri(opts.Endpoint), new AzureKeyCredential(opts.ApiKey));
            return azureClient.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });
```

- [ ] **Step 4: Wire FoundryLocal**

Replace lines 48–57:

```csharp
        services.AddKeyedSingleton<IChatClient>("FoundryLocal", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.FoundryLocal;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextSizingChatClient>>();

            var apiKey = string.IsNullOrWhiteSpace(opts.ApiKey) ? "notneeded" : opts.ApiKey;
            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey),
                new OpenAIClientOptions { Endpoint = new Uri(opts.Endpoint) });
            return client.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });
```

- [ ] **Step 5: Wire OllamaLocal**

Replace lines 60–65:

```csharp
        services.AddKeyedSingleton<IChatClient>("OllamaLocal", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.OllamaLocal;
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextSizingChatClient>>();

            return ((IChatClient)new OllamaApiClient(new Uri(opts.BaseUrl), opts.Model))
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });
```

- [ ] **Step 6: Wire GithubModels**

Replace lines 68–81:

```csharp
        services.AddKeyedSingleton<IChatClient>("GithubModels", (sp, _) =>
        {
            var opts = sp.GetRequiredService<IOptions<LlmGatewayOptions>>().Value.Providers.GithubModels;
            if (string.IsNullOrWhiteSpace(opts.ApiKey))
            {
                throw new InvalidOperationException("GithubModels requires API key in LlmGateway:Providers:GithubModels:ApiKey");
            }
            var tokenCounter  = sp.GetRequiredService<TokenCounting.ITokenCounter>();
            var compactor     = sp.GetRequiredService<IContextCompactor>();
            var sizingOptions = sp.GetRequiredService<IOptions<ContextSizingOptions>>();
            var sizingLogger  = sp.GetRequiredService<ILogger<ContextSizingChatClient>>();

            var client = new AzureOpenAIClient(
                new Uri(opts.Endpoint),
                new AzureKeyCredential(opts.ApiKey));
            return client.GetChatClient(opts.Model).AsIChatClient()
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    opts.MaxContextTokens, opts.ReservedOutputTokens, opts.Model, sizingLogger)
                .Build();
        });
```

- [ ] **Step 7: Build**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 8: Run all existing tests**

```
dotnet test Blaze.LlmGateway.Tests -v normal 2>&1 | Select-String -Pattern "passed|failed|error"
```

Expected: all previously passing tests still pass.

- [ ] **Step 9: Commit**

```
git add Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs
git commit -m "feat(context): wire ContextSizingChatClient into all keyed provider registrations"
```

---

## Task 7: Wire `ContextSizingChatClient` into dynamic Ollama path

**Files:**
- Modify: `Blaze.LlmGateway.Infrastructure/ModelSelectionResolver.cs`

`ModelSelectionResolver.ResolveAsync` constructs a fresh `OllamaApiClient` per request for catalog-discovered Ollama models. We need to wrap it with `ContextSizingChatClient` using limits from `ModelContextLimits.Lookup`, falling back to `OllamaLocalOptions.MaxContextTokens`.

- [ ] **Step 1: Inject sizing dependencies into `ModelSelectionResolver`**

The current constructor uses primary-constructor syntax. Expand it to accept new dependencies:

```csharp
// Blaze.LlmGateway.Infrastructure/ModelSelectionResolver.cs
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.TokenCounting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OllamaSharp;

namespace Blaze.LlmGateway.Infrastructure;

public sealed class ModelSelectionResolver(
    IServiceProvider serviceProvider,
    IModelCatalog modelCatalog,
    IOptions<LlmGatewayOptions> gatewayOptions,
    ITokenCounter tokenCounter,
    IContextCompactor compactor,
    IOptions<ContextSizingOptions> sizingOptions,
    ILogger<ModelSelectionResolver> logger,
    ILogger<ContextSizingChatClient> sizingLogger) : IModelSelectionResolver
{
    public async Task<IChatClient?> ResolveAsync(string modelId, CancellationToken cancellationToken = default)
    {
        var model = await modelCatalog.FindByIdAsync(modelId, cancellationToken);
        if (model is null)
        {
            return null;
        }

        if (string.Equals(model.Provider, "OllamaLocal", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(model.Endpoint))
        {
            logger.LogDebug("Resolving dynamic Ollama client for model {ModelId}", modelId);

            // Resolve context window: curated table → provider config fallback
            var ollamaOpts = gatewayOptions.Value.Providers.OllamaLocal;
            var (curatedWindow, _) = ModelContextLimits.Lookup(modelId);
            var contextWindow   = curatedWindow ?? ollamaOpts.MaxContextTokens;
            var reservedOutput  = ollamaOpts.ReservedOutputTokens;

            return ((IChatClient)new OllamaApiClient(new Uri(model.Endpoint), model.Id))
                .AsBuilder()
                .UseFunctionInvocation()
                .UseContextSizing(tokenCounter, compactor, sizingOptions,
                    contextWindow, reservedOutput, modelId, sizingLogger)
                .Build();
        }

        logger.LogDebug("Resolving keyed client {Provider} for model {ModelId}", model.Provider, modelId);
        return serviceProvider.GetKeyedService<IChatClient>(model.Provider);
    }
}
```

- [ ] **Step 2: Update DI registration of `ModelSelectionResolver` in `InfrastructureServiceExtensions.cs`**

The current registration (line 88) is:

```csharp
services.AddSingleton<IModelSelectionResolver, ModelSelectionResolver>();
```

Replace with explicit factory to supply the new constructor parameters:

```csharp
        services.AddSingleton<IModelSelectionResolver>(sp => new ModelSelectionResolver(
            sp,
            sp.GetRequiredService<IModelCatalog>(),
            sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
            sp.GetRequiredService<TokenCounting.ITokenCounter>(),
            sp.GetRequiredService<IContextCompactor>(),
            sp.GetRequiredService<IOptions<ContextSizingOptions>>(),
            sp.GetRequiredService<ILogger<ModelSelectionResolver>>(),
            sp.GetRequiredService<ILogger<ContextSizingChatClient>>()));
```

- [ ] **Step 3: Build**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 4: Run existing tests**

```
dotnet test Blaze.LlmGateway.Tests -v normal 2>&1 | Select-String -Pattern "passed|failed|error"
```

- [ ] **Step 5: Commit**

```
git add Blaze.LlmGateway.Infrastructure/ModelSelectionResolver.cs
git add Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs
git commit -m "feat(context): wire ContextSizingChatClient into dynamic Ollama path"
```

---

## Task 8: Update `LlmRoutingChatClient` to handle `ContextOverflowException`

**Files:**
- Modify: `Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs`
- Modify: `Blaze.LlmGateway.Tests/LlmRoutingChatClientTests.cs`

### Changes needed

1. Add `CanFit(string destination, int requiredTokens, ChatOptions?)` — synchronous, reads from `LlmGatewayOptions.Providers.{X}.MaxContextTokens`.
2. In `GetResponseAsync`: catch `ContextOverflowException` before the generic `Exception` catch and pass it to `TryFailoverAsync`.
3. In `TryFailoverAsync(overload)`: accept optional `ContextOverflowException?`; skip providers where `CanFit` returns false; throw `ContextOverflowException` (not `InvalidOperationException`) when chain is exhausted due to overflow.
4. In `TryGetFirstChunkAsync`: catch `ContextOverflowException` into a new `ThrownException` field so the streaming path can distinguish overflow failures from ordinary ones.
5. In `GetStreamingResponseAsyncImpl` / `TryFailoverStreamingAsync`: propagate overflow through the same size-aware path.

- [ ] **Step 1: Write failing tests**

Add to `Blaze.LlmGateway.Tests/LlmRoutingChatClientTests.cs`:

```csharp
// ──────────────────────────────────────────────────────────────
// Context overflow: primary overflows, fallback has larger window → uses fallback
// ──────────────────────────────────────────────────────────────

[Fact]
public async Task GetResponseAsync_PrimaryContextOverflow_RoutesToLargerWindowFallback()
{
    // Arrange
    var messages = new List<ChatMessage> { new(ChatRole.User, "big prompt") };
    var expectedResponse = new ChatResponse([new ChatMessage(ChatRole.Assistant, "answer")]);

    // Primary (FoundryLocal): throws ContextOverflowException with 90k tokens, budget 80k
    var primaryMock = new Mock<IChatClient>();
    primaryMock
        .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new ContextOverflowException("phi-4", requiredTokens: 90_000, budget: 80_000, []));

    // Fallback (AzureFoundry): succeeds, window=128k fits 90k tokens
    var fallbackMock = new Mock<IChatClient>();
    fallbackMock
        .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(expectedResponse);

    var services = new ServiceCollection();
    services.AddKeyedSingleton<IChatClient>("FoundryLocal", primaryMock.Object);
    services.AddKeyedSingleton<IChatClient>("AzureFoundry", fallbackMock.Object);
    var sp = services.BuildServiceProvider();

    var routingStrategyMock = new Mock<IRoutingStrategy>();
    routingStrategyMock
        .Setup(r => r.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(RouteDestination.FoundryLocal);

    var failoverStrategyMock = new Mock<IFailoverStrategy>();
    failoverStrategyMock
        .Setup(f => f.GetFailoverChainAsync(RouteDestination.FoundryLocal))
        .Returns([RouteDestination.AzureFoundry]);

    var availabilityMock = new Mock<IModelAvailabilityRegistry>();
    availabilityMock.Setup(a => a.IsProviderAvailable(It.IsAny<string>())).Returns(true);

    // LlmGatewayOptions: AzureFoundry.MaxContextTokens=128000 can fit 90k
    var gatewayOptions = Options.Create(new LlmGatewayOptions
    {
        Providers = new ProvidersOptions
        {
            AzureFoundry  = new AzureFoundryOptions  { MaxContextTokens = 128_000, ReservedOutputTokens = 4096 },
            FoundryLocal  = new FoundryLocalOptions  { MaxContextTokens = 80_000,  ReservedOutputTokens = 4096 },
            GithubModels  = new GithubModelsOptions  { MaxContextTokens = 128_000, ReservedOutputTokens = 4096 },
            OllamaLocal   = new OllamaLocalOptions   { MaxContextTokens = 32_768,  ReservedOutputTokens = 2048 },
        }
    });
    var sizingOptions = Options.Create(new ContextSizingOptions { DefaultReservedOutputTokens = 1024 });

    var sut = new LlmRoutingChatClient(
        primaryMock.Object,  // innerClient (fallback of last resort)
        sp,
        routingStrategyMock.Object,
        failoverStrategyMock.Object,
        availabilityMock.Object,
        gatewayOptions,
        sizingOptions,
        new NullLogger<LlmRoutingChatClient>());

    // Act
    var result = await sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

    // Assert
    result.Should().BeSameAs(expectedResponse);
    fallbackMock.Verify(c => c.GetResponseAsync(
        It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions>(), It.IsAny<CancellationToken>()),
        Times.Once);
}

[Fact]
public async Task GetResponseAsync_AllProvidersContextOverflow_ThrowsContextOverflowException()
{
    var messages = new List<ChatMessage> { new(ChatRole.User, "enormous prompt") };

    var primaryMock = new Mock<IChatClient>();
    primaryMock
        .Setup(c => c.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
        .ThrowsAsync(new ContextOverflowException("phi-4", 200_000, 80_000, []));

    var services = new ServiceCollection();
    services.AddKeyedSingleton<IChatClient>("FoundryLocal", primaryMock.Object);
    var sp = services.BuildServiceProvider();

    var routingStrategyMock = new Mock<IRoutingStrategy>();
    routingStrategyMock
        .Setup(r => r.ResolveAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync(RouteDestination.FoundryLocal);

    var failoverStrategyMock = new Mock<IFailoverStrategy>();
    // AzureFoundry in chain but its window (128k) < required (200k)
    failoverStrategyMock
        .Setup(f => f.GetFailoverChainAsync(RouteDestination.FoundryLocal))
        .Returns([RouteDestination.AzureFoundry]);

    var availabilityMock = new Mock<IModelAvailabilityRegistry>();
    availabilityMock.Setup(a => a.IsProviderAvailable(It.IsAny<string>())).Returns(true);

    var gatewayOptions = Options.Create(new LlmGatewayOptions
    {
        Providers = new ProvidersOptions
        {
            AzureFoundry = new AzureFoundryOptions { MaxContextTokens = 128_000, ReservedOutputTokens = 4096 },
            FoundryLocal = new FoundryLocalOptions  { MaxContextTokens = 80_000,  ReservedOutputTokens = 4096 },
        }
    });
    var sizingOptions = Options.Create(new ContextSizingOptions { DefaultReservedOutputTokens = 1024 });

    var sut = new LlmRoutingChatClient(
        primaryMock.Object,
        sp,
        routingStrategyMock.Object,
        failoverStrategyMock.Object,
        availabilityMock.Object,
        gatewayOptions,
        sizingOptions,
        new NullLogger<LlmRoutingChatClient>());

    var act = () => sut.GetResponseAsync(messages, new ChatOptions(), CancellationToken.None);

    await act.Should().ThrowAsync<ContextOverflowException>()
        .Where(ex => ex.RequiredTokens == 200_000);
}
```

- [ ] **Step 2: Run tests to confirm they fail**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~LlmRoutingChatClientTests" 2>&1 | Select-String "error|failed"
```

Expected: compiler errors — `LlmRoutingChatClient` constructor mismatch.

- [ ] **Step 3: Implement the changes**

Replace the full `LlmRoutingChatClient.cs`:

```csharp
// Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs
using System.Runtime.CompilerServices;
using Blaze.LlmGateway.Core;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Core.ModelCatalog;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Blaze.LlmGateway.Infrastructure;

public class LlmRoutingChatClient : DelegatingChatClient
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRoutingStrategy _routingStrategy;
    private readonly IFailoverStrategy _failoverStrategy;
    private readonly IModelAvailabilityRegistry _availabilityRegistry;
    private readonly IOptions<LlmGatewayOptions> _gatewayOptions;
    private readonly IOptions<ContextSizingOptions> _sizingOptions;
    private readonly ILogger<LlmRoutingChatClient> _logger;

    public LlmRoutingChatClient(
        IChatClient innerClient,
        IServiceProvider serviceProvider,
        IRoutingStrategy routingStrategy,
        IFailoverStrategy failoverStrategy,
        IModelAvailabilityRegistry availabilityRegistry,
        IOptions<LlmGatewayOptions> gatewayOptions,
        IOptions<ContextSizingOptions> sizingOptions,
        ILogger<LlmRoutingChatClient> logger) : base(innerClient)
    {
        _serviceProvider = serviceProvider;
        _routingStrategy = routingStrategy;
        _failoverStrategy = failoverStrategy;
        _availabilityRegistry = availabilityRegistry;
        _gatewayOptions = gatewayOptions;
        _sizingOptions = sizingOptions;
        _logger = logger;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("🔀 ResolveTargetClient: Getting routing decision...");
        var targetClient = await ResolveTargetClientAsync(chatMessages, cancellationToken);
        _logger.LogInformation("🎯 Routing request to: {TargetClient}", targetClient.GetType().Name);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var response = await targetClient.GetResponseAsync(chatMessages, options, cancellationToken);
            sw.Stop();
            _logger.LogInformation("✅ Provider responded in {ElapsedMs}ms", sw.ElapsedMilliseconds);
            return response;
        }
        catch (ContextOverflowException coe)
        {
            _logger.LogWarning(
                "⚠️ Context overflow on primary provider ({ModelId}): {Required} tokens > budget {Budget}; attempting size-aware failover",
                coe.ModelId, coe.RequiredTokens, coe.Budget);
            return await TryFailoverAsync(chatMessages, options, cancellationToken, coe);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "⚠️ Provider failed; attempting failover...");
            return await TryFailoverAsync(chatMessages, options, cancellationToken);
        }
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
        => GetStreamingResponseAsyncImpl(chatMessages, options, cancellationToken);

    private async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsyncImpl(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _logger.LogDebug("🔀 ResolveTargetClient: Getting routing decision...");
        var targetClient = await ResolveTargetClientAsync(chatMessages, cancellationToken);
        _logger.LogInformation("🎯 Routing streaming request to: {TargetClient}", targetClient.GetType().Name);

        var result = await TryGetFirstChunkAsync(targetClient, chatMessages, options, cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("⚠️ Primary provider failed before first chunk — attempting failover chain...");

            var overflow = result.ThrownException as ContextOverflowException;
            await foreach (var update in TryFailoverStreamingAsync(chatMessages, options, cancellationToken, overflow))
                yield return update;

            yield break;
        }

        yield return result.FirstChunk;
        _logger.LogDebug("  ├─ First chunk received");

        var enumerator = result.Enumerator!;
        var chunkCount = 1;
        while (true)
        {
            bool hasMore = false;
            bool streamFailed = false;
            try
            {
                hasMore = await enumerator.MoveNextAsync();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "⚠️ Mid-stream failure from primary provider — ending stream");
                streamFailed = true;
            }

            if (streamFailed || !hasMore)
            {
                await enumerator.DisposeAsync();
                _logger.LogInformation("✅ Streaming complete - {ChunkCount} updates", chunkCount);
                yield break;
            }

            chunkCount++;
            yield return enumerator.Current;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Failover (non-streaming)
    // ──────────────────────────────────────────────────────────────

    private async Task<ChatResponse> TryFailoverAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken,
        ContextOverflowException? overflow = null)
    {
        var destination = await _routingStrategy.ResolveAsync(chatMessages, cancellationToken);
        var fallbackChain = _failoverStrategy.GetFailoverChainAsync(destination);
        ContextOverflowException? lastOverflow = overflow;

        foreach (var fallback in fallbackChain)
        {
            if (!_availabilityRegistry.IsProviderAvailable(fallback.ToString()))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} is marked unavailable; skipping", fallback);
                continue;
            }

            // Skip providers whose context window is too small for the known required token count.
            if (lastOverflow is not null && !CanFit(fallback.ToString(), lastOverflow.RequiredTokens, options))
            {
                _logger.LogDebug(
                    "  ├─ Failover provider {Fallback} window too small ({Required} > budget); skipping",
                    fallback, lastOverflow.RequiredTokens);
                lastOverflow = lastOverflow.WithAttempted(fallback.ToString());
                continue;
            }

            _logger.LogInformation("🔄 Trying failover provider: {Fallback}", fallback);
            var fallbackClient = _serviceProvider.GetKeyedService<IChatClient>(fallback.ToString());
            if (fallbackClient is null)
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} not registered; skipping", fallback);
                continue;
            }

            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var response = await fallbackClient.GetResponseAsync(chatMessages, options, cancellationToken);
                sw.Stop();
                _logger.LogInformation("✅ Failover succeeded with {Fallback} in {ElapsedMs}ms", fallback, sw.ElapsedMilliseconds);
                return response;
            }
            catch (ContextOverflowException coe)
            {
                _logger.LogWarning("  ├─ Failover provider {Fallback} context overflow ({Required} > {Budget}); trying next",
                    fallback, coe.RequiredTokens, coe.Budget);
                lastOverflow = (lastOverflow ?? coe).WithAttempted(fallback.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "  ├─ Failover provider {Fallback} also failed", fallback);
            }
        }

        _logger.LogError("❌ All failover providers exhausted");

        if (lastOverflow is not null)
            throw lastOverflow;

        throw new InvalidOperationException("All providers in failover chain failed");
    }

    // ──────────────────────────────────────────────────────────────
    // Failover (streaming)
    // ──────────────────────────────────────────────────────────────

    private async IAsyncEnumerable<ChatResponseUpdate> TryFailoverStreamingAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        ContextOverflowException? overflow = null)
    {
        var destination = await _routingStrategy.ResolveAsync(chatMessages, cancellationToken);
        var fallbackChain = _failoverStrategy.GetFailoverChainAsync(destination);
        ContextOverflowException? lastOverflow = overflow;

        foreach (var fallback in fallbackChain)
        {
            if (!_availabilityRegistry.IsProviderAvailable(fallback.ToString()))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} is marked unavailable; skipping", fallback);
                continue;
            }

            if (lastOverflow is not null && !CanFit(fallback.ToString(), lastOverflow.RequiredTokens, options))
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} window too small; skipping", fallback);
                lastOverflow = lastOverflow.WithAttempted(fallback.ToString());
                continue;
            }

            _logger.LogInformation("🔄 Trying failover provider (streaming): {Fallback}", fallback);
            var fallbackClient = _serviceProvider.GetKeyedService<IChatClient>(fallback.ToString());
            if (fallbackClient is null)
            {
                _logger.LogDebug("  ├─ Failover provider {Fallback} not registered; skipping", fallback);
                continue;
            }

            var probe = await TryGetFirstChunkAsync(fallbackClient, chatMessages, options, cancellationToken);
            if (!probe.Success)
            {
                if (probe.ThrownException is ContextOverflowException coe)
                {
                    _logger.LogWarning("  ├─ Failover provider {Fallback} context overflow; trying next", fallback);
                    lastOverflow = (lastOverflow ?? coe).WithAttempted(fallback.ToString());
                }
                else
                {
                    _logger.LogWarning("  ├─ Failover provider {Fallback} failed before first chunk; trying next", fallback);
                }
                continue;
            }

            _logger.LogInformation("✅ Failover streaming: first chunk received from {Fallback}", fallback);
            yield return probe.FirstChunk;

            var enumerator = probe.Enumerator!;
            int chunkCount = 1;
            while (true)
            {
                bool hasMore = false;
                bool midStreamFailed = false;
                try
                {
                    hasMore = await enumerator.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "  ├─ Failover provider {Fallback} failed mid-stream; ending", fallback);
                    midStreamFailed = true;
                }

                if (midStreamFailed || !hasMore)
                {
                    await enumerator.DisposeAsync();
                    _logger.LogInformation("✅ Failover streaming complete with {Fallback} ({ChunkCount} chunks)", fallback, chunkCount);
                    yield break;
                }

                chunkCount++;
                yield return enumerator.Current;
            }
        }

        _logger.LogError("❌ All failover providers exhausted (streaming); throwing error");

        if (lastOverflow is not null)
            throw lastOverflow;

        throw new InvalidOperationException("All providers in failover chain failed during streaming");
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private async Task<IChatClient> ResolveTargetClientAsync(
        IEnumerable<ChatMessage> messages,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("🔀 Routing strategy resolving...");
        var destination = await _routingStrategy.ResolveAsync(messages, cancellationToken);
        _logger.LogDebug("  ├─ Routing strategy decided: {Destination}", destination);

        if (!_availabilityRegistry.IsProviderAvailable(destination.ToString()))
        {
            _logger.LogWarning("❌ Destination '{Destination}' is currently unavailable. Deferring to failover chain.", destination);
            return new UnavailableChatClient($"Provider '{destination}' is currently unavailable.");
        }

        var client = _serviceProvider.GetKeyedService<IChatClient>(destination.ToString());
        if (client is null)
        {
            _logger.LogWarning("❌ No client registered for destination '{Destination}'. Using fallback to InnerClient.", destination);
            return InnerClient;
        }

        _logger.LogDebug("  └─ Found registered client for {Destination}", destination);
        return client;
    }

    /// <summary>
    /// Returns true if <paramref name="destination"/> has a context window large enough
    /// to accommodate <paramref name="requiredTokens"/>. Unknown destinations are optimistically accepted.
    /// </summary>
    private bool CanFit(string destination, int requiredTokens, ChatOptions? options)
    {
        var providers = _gatewayOptions.Value.Providers;
        var (maxContext, reservedOutput) = destination switch
        {
            "AzureFoundry" => (providers.AzureFoundry.MaxContextTokens,  providers.AzureFoundry.ReservedOutputTokens),
            "FoundryLocal"  => (providers.FoundryLocal.MaxContextTokens,  providers.FoundryLocal.ReservedOutputTokens),
            "OllamaLocal"   => (providers.OllamaLocal.MaxContextTokens,   providers.OllamaLocal.ReservedOutputTokens),
            "GithubModels"  => (providers.GithubModels.MaxContextTokens,  providers.GithubModels.ReservedOutputTokens),
            _ => (int.MaxValue, 0)   // unknown → optimistic
        };
        var reserved = options?.MaxOutputTokens ?? reservedOutput;
        return requiredTokens <= (maxContext - reserved);
    }

    /// <summary>
    /// Probes for the first streaming chunk without committing.
    /// Catches <see cref="ContextOverflowException"/> into <see cref="FirstChunkResult.ThrownException"/>
    /// so the caller can distinguish overflow from ordinary connection failures.
    /// </summary>
    private static async Task<FirstChunkResult> TryGetFirstChunkAsync(
        IChatClient client,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options,
        CancellationToken ct)
    {
        var enumerator = client.GetStreamingResponseAsync(messages, options, ct)
                               .GetAsyncEnumerator(ct);
        try
        {
            if (!await enumerator.MoveNextAsync())
            {
                await enumerator.DisposeAsync();
                return FirstChunkResult.Failed;
            }
            return new FirstChunkResult(true, enumerator.Current, enumerator);
        }
        catch (ContextOverflowException ex)
        {
            await enumerator.DisposeAsync();
            return new FirstChunkResult(false, ThrownException: ex);
        }
        catch
        {
            await enumerator.DisposeAsync();
            return FirstChunkResult.Failed;
        }
    }
}

/// <summary>Encapsulates the result of probing for the first streaming chunk.</summary>
internal record FirstChunkResult(
    bool Success,
    ChatResponseUpdate FirstChunk = default!,
    IAsyncEnumerator<ChatResponseUpdate>? Enumerator = null,
    Exception? ThrownException = null)
{
    public static readonly FirstChunkResult Failed = new(false);
}
```

- [ ] **Step 4: Update DI factory for `LlmRoutingChatClient` in `InfrastructureServiceExtensions.cs`**

In `AddLlmInfrastructure`, find the `IChatClient` singleton registration (around line 117). Replace:

```csharp
            IChatClient router = new LlmRoutingChatClient(fallback, sp, strategy, failoverStrategy, availabilityRegistry, routerLogger);
```

with:

```csharp
            IChatClient router = new LlmRoutingChatClient(
                fallback, sp, strategy, failoverStrategy, availabilityRegistry,
                sp.GetRequiredService<IOptions<LlmGatewayOptions>>(),
                sp.GetRequiredService<IOptions<ContextSizingOptions>>(),
                routerLogger);
```

- [ ] **Step 5: Run all tests**

```
dotnet test Blaze.LlmGateway.Tests -v normal 2>&1 | Select-String -Pattern "passed|failed|error"
```

Expected: all tests including new overflow tests pass.

- [ ] **Step 6: Build clean**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 7: Commit**

```
git add Blaze.LlmGateway.Infrastructure/LlmRoutingChatClient.cs
git add Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs
git add Blaze.LlmGateway.Tests/LlmRoutingChatClientTests.cs
git commit -m "feat(routing): handle ContextOverflowException with size-aware failover in LlmRoutingChatClient"
```

---

## Task 9: Map `ContextOverflowException` → HTTP 413 in `ChatCompletionsEndpoint`

**Files:**
- Modify: `Blaze.LlmGateway.Api/ChatCompletionsEndpoint.cs`

- [ ] **Step 1: Add the using**

At the top of `ChatCompletionsEndpoint.cs`, add:

```csharp
using Blaze.LlmGateway.Infrastructure.ContextHandling;
```

- [ ] **Step 2: Update `CreateProviderErrorResult`**

Replace the existing `CreateProviderErrorResult` method (lines 336–360) with the version below that handles `ContextOverflowException` first:

```csharp
    private static IResult CreateProviderErrorResult(string model, Exception? exception)
    {
        // Context overflow — no provider's window is large enough for this prompt.
        if (exception is ContextOverflowException coe)
        {
            return Results.Json(
                new
                {
                    error = new
                    {
                        message = $"The prompt requires {coe.RequiredTokens} tokens but the largest available " +
                                  $"context window could only accommodate {coe.Budget} tokens. " +
                                  $"Please reduce the prompt length.",
                        type    = "context_length_exceeded",
                        code    = "context_length_exceeded",
                        param   = (string?)null,
                        required_tokens        = coe.RequiredTokens,
                        largest_window_budget  = coe.Budget,
                        attempted_destinations = coe.AttemptedDestinations,
                    }
                },
                statusCode: StatusCodes.Status413RequestEntityTooLarge);
        }

        var statusCode = exception is ClientResultException { Status: 404 }
            ? StatusCodes.Status404NotFound
            : exception is InvalidOperationException invalidOperation &&
              invalidOperation.Message.Contains("currently unavailable", StringComparison.OrdinalIgnoreCase)
                ? StatusCodes.Status503ServiceUnavailable
                : StatusCodes.Status502BadGateway;
        var code = statusCode switch
        {
            StatusCodes.Status404NotFound => "model_not_found",
            StatusCodes.Status503ServiceUnavailable => "model_unavailable",
            _ => "provider_error"
        };
        var message = statusCode switch
        {
            StatusCodes.Status404NotFound => $"Model or deployment '{model}' was not found by the configured provider.",
            StatusCodes.Status503ServiceUnavailable => exception?.Message ?? $"Model '{model}' is currently unavailable.",
            _ => $"The configured provider failed while processing model '{model}'."
        };

        return Results.Json(
            new ErrorResponse(new ErrorDetail(message, "provider_error", code)),
            statusCode: statusCode);
    }
```

- [ ] **Step 3: Handle `ContextOverflowException` in non-streaming path**

In `HandleNonStreamingAsync`, the `catch (Exception ex)` block already calls `CreateProviderErrorResult(model, ex)`, which now handles `ContextOverflowException` — no additional change needed.

- [ ] **Step 4: Handle in streaming path**

In `HandleStreamingAsync`, the first-chunk probe path already calls `CreateProviderErrorResult(model, firstChunk.Exception)`. The mid-stream exception is logged but not re-thrown (streaming has already started). This is correct — if overflow surfaces mid-stream (impossible in practice since the pre-flight throws before any yield), the stream simply ends. No additional change needed.

- [ ] **Step 5: Build**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
```

- [ ] **Step 6: Run all tests**

```
dotnet test Blaze.LlmGateway.Tests -v normal 2>&1 | Select-String -Pattern "passed|failed|error"
```

- [ ] **Step 7: Commit**

```
git add Blaze.LlmGateway.Api/ChatCompletionsEndpoint.cs
git commit -m "feat(api): map ContextOverflowException to HTTP 413 in chat completions endpoint"
```

---

## Task 10: Integration test — large prompt returns HTTP 413

**Files:**
- Create: `Blaze.LlmGateway.Tests/ContextSizingIntegrationTests.cs`

This test spins up the real `WebApplicationFactory`, posts a prompt whose token count exceeds every configured provider's window, and asserts 413. Provider windows are forced to a tiny value via `IOptions` overrides in the test server configuration.

- [ ] **Step 1: Write the test**

```csharp
// Blaze.LlmGateway.Tests/ContextSizingIntegrationTests.cs
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Blaze.LlmGateway.Tests;

/// <summary>
/// Integration tests for context-size enforcement at the HTTP layer.
/// Uses a real WebApplicationFactory with provider windows overridden to tiny values.
/// </summary>
public class ContextSizingIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ContextSizingIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Override LlmGatewayOptions so ALL providers have a 1-token window.
                // Any real prompt will overflow every provider.
                services.PostConfigure<LlmGatewayOptions>(opts =>
                {
                    opts.ContextSizing.Enabled = true;
                    opts.Providers.AzureFoundry.MaxContextTokens  = 1;
                    opts.Providers.FoundryLocal.MaxContextTokens   = 1;
                    opts.Providers.OllamaLocal.MaxContextTokens    = 1;
                    opts.Providers.GithubModels.MaxContextTokens   = 1;
                });
            });
        });
    }

    [Fact]
    public async Task PostChatCompletion_PromptExceedsAllWindows_Returns413()
    {
        var client = _factory.CreateClient();

        var payload = new
        {
            model    = "gpt-4o",
            messages = new[]
            {
                new { role = "user", content = "Hello world" }
            },
            stream = false
        };

        var response = await client.PostAsJsonAsync("/v1/chat/completions", payload);

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("error").GetProperty("type").GetString()
            .Should().Be("context_length_exceeded");
        body.GetProperty("error").GetProperty("required_tokens").GetInt32()
            .Should().BeGreaterThan(0);
    }
}
```

- [ ] **Step 2: Run the test**

```
dotnet test Blaze.LlmGateway.Tests --filter "FullyQualifiedName~ContextSizingIntegrationTests" -v normal
```

Expected: test passes (413 returned).

If the test fails because providers are not configured (no real API keys), the startup probe may mark all providers unavailable before the request reaches sizing. In that case, add a `Skip` attribute with a note, or ensure the test overrides availability as well:

```csharp
// Add to ConfigureServices in the factory override, if needed:
services.PostConfigure<ModelAvailabilityOptions>(opts => opts.Enabled = false);
```

- [ ] **Step 3: Build and run full suite**

```
dotnet build Blaze.LlmGateway.sln -warnaserror
dotnet test Blaze.LlmGateway.Tests -v normal 2>&1 | Select-String -Pattern "passed|failed|error"
```

- [ ] **Step 4: Commit**

```
git add Blaze.LlmGateway.Tests/ContextSizingIntegrationTests.cs
git commit -m "test(integration): add HTTP 413 integration test for context overflow"
```

---

## Self-Review Checklist

Run through these before marking the feature done:

- [ ] Every keyed provider (`AzureFoundry`, `FoundryLocal`, `OllamaLocal`, `GithubModels`) is wrapped with `UseContextSizing()` in `InfrastructureServiceExtensions.AddLlmProviders`.
- [ ] Dynamic Ollama path in `ModelSelectionResolver.ResolveAsync` wraps with `UseContextSizing()`.
- [ ] `LlmRoutingChatClient` constructor takes `IOptions<LlmGatewayOptions>` and `IOptions<ContextSizingOptions>` — DI factory in `AddLlmInfrastructure` passes them.
- [ ] `ContextOverflowException.AttemptedDestinations` grows as providers are skipped/tried.
- [ ] Streaming path: `ContextOverflowException` thrown inside `ContextSizingChatClient.StreamingImpl` before first `yield` — caught by `TryGetFirstChunkAsync` as `ThrownException`, propagated to `TryFailoverStreamingAsync`.
- [ ] HTTP 413 body has `type = "context_length_exceeded"`, `required_tokens`, `largest_window_budget`.
- [ ] `dotnet build -warnaserror` clean.
- [ ] `dotnet test` ≥ 95% coverage on new files.
