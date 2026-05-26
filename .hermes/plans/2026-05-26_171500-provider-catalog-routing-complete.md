# Provider Catalog Routing — Phase 2 & 3 Implementation Plan

> **For Hermes:** Use subagent-driven-development to implement this plan task-by-task.

**Goal:** Complete the provider catalog routing engine by (a) wiring `CatalogModel` into `ModelSelectionResolver` so virtual models route through the catalog, and (b) adding health probes + circuit breaker + fallback chain.

**Architecture:** The catalog is already built (types, strategies, filter). Phase 2 plugs it into the request path. Phase 3 adds health observability that feeds back into routing decisions.

**Tech Stack:** .NET 10, MEAI, xUnit, Moq

**Branch:** `feature/provider-catalog-routing`
**Base:** `master`

---

## Phase 2: Virtual Model Binding

### Task 2.1: Add CatalogModelRouter middleware

**Objective:** Create a new middleware that routes virtual model requests through the provider catalog when `VirtualModelOptions.CatalogModel` is set.

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/Catalog/CatalogModelRouter.cs`
- Modify: `Blaze.LlmGateway.Infrastructure/InfrastructureServiceExtensions.cs`
- Test: `Blaze.LlmGateway.Tests/Catalog/CatalogModelRouterTests.cs`

**Step 1: Create CatalogModelRouter**

```csharp
namespace Blaze.LlmGateway.Infrastructure.Catalog;

using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Blaze.LlmGateway.Infrastructure.ContextHandling;
using Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class CatalogModelRouter(
    IProviderCatalog catalog,
    IRoutingStrategyResolver strategyResolver,
    IServiceProvider serviceProvider,
    ITokenCounter tokenCounter,
    IContextCompactor compactor,
    IOptions<ContextSizingOptions> sizingOptions,
    ILogger<CatalogModelRouter> logger,
    ILogger<ContextSizingChatClient> sizingLogger)
{
    public async Task<IChatClient?> ResolveAsync(
        string modelId,
        VirtualModelOptions virtualModel,
        RoutingContext routingContext,
        CancellationToken cancellationToken = default)
    {
        var catalogModel = virtualModel.CatalogModel;
        if (string.IsNullOrWhiteSpace(catalogModel))
            return null; // Not a catalog-routed model — caller falls back

        var route = catalog.GetRoute(catalogModel);
        if (route is null)
        {
            logger.LogWarning("Catalog model '{CatalogModel}' (from virtual model '{ModelId}') has no route configured", catalogModel, modelId);
            return null;
        }

        // Get eligible deployments, filtered by health + capabilities
        var candidates = catalog.GetDeploymentsForModel(catalogModel);
        if (candidates.Count == 0)
        {
            logger.LogWarning("No deployments found for catalog model '{CatalogModel}'", catalogModel);
            return null;
        }

        // Apply health-aware filter (shared pre-filter from Phase 1)
        var healthy = HealthAwareRoutingFilter.Filter(candidates, catalog, routingContext);
        if (healthy.Count == 0)
        {
            logger.LogWarning("All deployments for '{CatalogModel}' are unhealthy or filtered out", catalogModel);
            // Try fallbacks from the route
            healthy = ResolveFallbacks(route, routingContext);
            if (healthy.Count == 0)
            {
                logger.LogError("No fallback deployments available for '{CatalogModel}'", catalogModel);
                return null;
            }
        }

        // Resolve strategy and select deployment
        var strategy = strategyResolver.Resolve(route.Strategy);
        if (strategy is null)
        {
            logger.LogError("Unknown routing strategy '{Strategy}' for model '{CatalogModel}'", route.Strategy, catalogModel);
            return null;
        }

        var selected = strategy.Select(healthy, routingContext);
        if (selected is null)
        {
            logger.LogWarning("Strategy '{Strategy}' returned no deployment for '{CatalogModel}'", route.Strategy, catalogModel);
            return null;
        }

        logger.LogDebug(
            "Catalog routing: model={CatalogModel} virtual={ModelId} strategy={Strategy} deployment={Deployment}",
            catalogModel, modelId, route.Strategy, selected.Name);

        // Resolve IChatClient for the selected deployment
        return ResolveChatClient(selected, routingContext);
    }

    private IReadOnlyList<ProviderDeployment> ResolveFallbacks(
        CatalogModelRoute route, RoutingContext routingContext)
    {
        if (route.Fallbacks.Length == 0) return [];

        var fallbackDeployments = new List<ProviderDeployment>();
        foreach (var fbName in route.Fallbacks)
        {
            var dep = catalog.GetDeployment(fbName);
            if (dep is not null && catalog.IsHealthy(fbName))
                fallbackDeployments.Add(dep);
        }
        return fallbackDeployments;
    }

    private IChatClient? ResolveChatClient(
        ProviderDeployment deployment, RoutingContext routingContext)
    {
        var client = serviceProvider.GetKeyedService<IChatClient>(deployment.Provider);
        if (client is null)
        {
            logger.LogWarning("No keyed IChatClient registered for provider '{Provider}'", deployment.Provider);
            return null;
        }

        // Wrap with context sizing
        return client
            .AsBuilder()
            .UseFunctionInvocation()
            .UseContextSizing(tokenCounter, compactor, sizingOptions,
                deployment.MaxContextTokens, 0, deployment.ModelName, sizingLogger)
            .Build();
    }
}
```

**Step 2: Create IRoutingStrategyResolver interface**

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/RoutingStrategies/Catalog/IRoutingStrategyResolver.cs`

```csharp
namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

using Blaze.LlmGateway.Core.Catalog;

/// <summary>Resolves a named strategy to its implementation.</summary>
public interface IRoutingStrategyResolver
{
    IRoutingStrategy? Resolve(string strategyName);
}
```

**Step 3: Create RoutingStrategyResolver implementation**

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/RoutingStrategies/Catalog/RoutingStrategyResolver.cs`

```csharp
namespace Blaze.LlmGateway.Infrastructure.RoutingStrategies.Catalog;

using Microsoft.Extensions.DependencyInjection;

public sealed class RoutingStrategyResolver(IServiceProvider serviceProvider) : IRoutingStrategyResolver
{
    private static readonly Dictionary<string, Type> StrategyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["round_robin"] = typeof(RoundRobinStrategy),
        ["shuffle"] = typeof(ShuffleStrategy),
        ["latency"] = typeof(LatencyStrategy),
        ["cost"] = typeof(CostStrategy),
        ["least_busy"] = typeof(LeastBusyStrategy),
    };

    public IRoutingStrategy? Resolve(string strategyName)
    {
        if (string.IsNullOrWhiteSpace(strategyName)) return null;
        if (!StrategyMap.TryGetValue(strategyName, out var type)) return null;
        return serviceProvider.GetService(type) as IRoutingStrategy;
    }
}
```

**Step 4: Add IRoutingStrategy interface (if not already present in Core)**

Check if already exists in `Blaze.LlmGateway.Core/Routing/`. If not, create it:

**Files:**
- Create: `Blaze.LlmGateway.Core/Routing/IRoutingStrategy.cs`
- Create: `Blaze.LlmGateway.Core/Routing/RoutingContext.cs`

`IRoutingStrategy.cs`:
```csharp
namespace Blaze.LlmGateway.Core.Routing;

using Blaze.LlmGateway.Core.Catalog;

public interface IRoutingStrategy
{
    string Name { get; }
    ProviderDeployment? Select(IReadOnlyList<ProviderDeployment> candidates, RoutingContext context);
}
```

`RoutingContext.cs`:
```csharp
namespace Blaze.LlmGateway.Core.Routing;

public sealed record RoutingContext(
    string ModelId,
    int EstimatedInputTokens,
    bool StreamingRequested,
    bool ToolsRequested,
    bool VisionRequested,
    CancellationToken CancellationToken);
```

**Step 5: Register in DI**

In `InfrastructureServiceExtensions.cs`, add:
```csharp
services.AddSingleton<CatalogModelRouter>();
services.AddSingleton<IRoutingStrategyResolver, RoutingStrategyResolver>();

// Register catalog routing strategies as singletons
services.AddSingleton<RoundRobinStrategy>();
services.AddSingleton<ShuffleStrategy>();
services.AddSingleton<LatencyStrategy>();
services.AddSingleton<CostStrategy>();
services.AddSingleton<LeastBusyStrategy>();
```

**Step 6: Modify ModelSelectionResolver**

In `ModelSelectionResolver.ResolveAsync`, add catalog routing BEFORE the virtual model fallback:

```csharp
// After offline-only check, before IsVirtualModel check:
if (!string.IsNullOrWhiteSpace(virtualModel?.CatalogModel))
{
    var routingContext = new RoutingContext(
        modelId,
        EstimatedInputTokens: 0,
        StreamingRequested: false,
        ToolsRequested: false,
        VisionRequested: false,
        cancellationToken);
    
    var catalogClient = await catalogRouter.ResolveAsync(modelId, virtualModel, routingContext, cancellationToken);
    if (catalogClient is not null)
        return catalogClient;
    
    logger.LogInformation("Catalog routing returned no client for {ModelId}; falling back to legacy path", modelId);
}
```

**Step 7: Write tests**

Test file: `Blaze.LlmGateway.Tests/Catalog/CatalogModelRouterTests.cs`

Test cases:
- Returns null when CatalogModel is null/empty (no-op)
- Returns null when route is missing
- Returns null when no healthy deployments
- Returns null when strategy is unknown
- Returns IChatClient when everything resolves
- Fallback chain is tried when primary deployments are unhealthy
- Logs warnings for various failure modes

**Step 8: Run tests and fix**

Run: `dotnet test Blaze.LlmGateway.Tests --filter "CatalogModelRouter" -v`
Expected: all new tests pass

**Step 9: Commit**

```bash
git add -A
git commit -m "feat: wire CatalogModel into ModelSelectionResolver"
```

---

## Phase 3: Health Routing

### Task 3.1: Create HealthProbeService

**Objective:** Background service that periodically probes all catalog deployments and reports health states.

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/Catalog/HealthProbeService.cs`
- Test: `Blaze.LlmGateway.Tests/Catalog/HealthProbeServiceTests.cs`

**Step 1: Create HealthProbeService**

```csharp
namespace Blaze.LlmGateway.Infrastructure.Catalog;

using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class HealthProbeService(
    IProviderCatalog catalog,
    IOptions<ProviderCatalogOptions> options,
    ILogger<HealthProbeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.HealthCheckIntervalSeconds);
        var probeInterval = TimeSpan.FromSeconds(options.Value.RecoveryIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(interval, stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;

            await ProbeAsync(stoppingToken);
        }
    }

    public async Task ProbeAsync(CancellationToken cancellationToken)
    {
        var deployments = catalog.GetAllDeployments();
        foreach (var dep in deployments)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                var healthy = await ProbeDeploymentAsync(dep, cts.Token);
                catalog.ReportHealth(dep.Name, healthy);
                logger.LogDebug("Health probe for '{Deployment}': {Status}", dep.Name, healthy ? "healthy" : "unhealthy");
            }
            catch (OperationCanceledException)
            {
                catalog.ReportHealth(dep.Name, false);
                logger.LogWarning("Health probe for '{Deployment}' timed out", dep.Name);
            }
            catch (Exception ex)
            {
                catalog.ReportHealth(dep.Name, false);
                logger.LogWarning(ex, "Health probe for '{Deployment}' failed", dep.Name);
            }
        }
    }

    private async Task<bool> ProbeDeploymentAsync(ProviderDeployment deployment, CancellationToken ct)
    {
        var client = ResolveClient(deployment);
        if (client is null) return false;

        // Lightweight probe — simple chat completion with minimal tokens
        var response = await client.CompleteAsync(
            [new ChatMessage(ChatRole.User, "ping")],
            new ChatOptions { MaxOutputTokens = 1 },
            ct);
        
        return response?.Message?.Content is not null;
    }

    private IChatClient? ResolveClient(ProviderDeployment dep)
    {
        // Use the same resolution as CatalogModelRouter
        // (simplified - no context sizing needed for probes)
        return null; // Placeholder — injected via service provider
    }
}
```

**Step 2: Register HealthProbeService**

In `InfrastructureServiceExtensions.cs`:
```csharp
services.AddHostedService<HealthProbeService>();
```

**Step 3: Add CircuitBreakerClient middleware**

**Files:**
- Create: `Blaze.LlmGateway.Infrastructure/Catalog/CircuitBreakerChatClient.cs`

```csharp
namespace Blaze.LlmGateway.Infrastructure.Catalog;

using Blaze.LlmGateway.Core.Catalog;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

/// <summary>
/// MEAI DelegatingChatClient that tracks failures and reports to IProviderCatalog.
/// When the catalog marks a deployment unhealthy, this client short-circuits
/// instead of making the call.
/// </summary>
public sealed class CircuitBreakerChatClient(
    IProviderCatalog catalog,
    string deploymentName,
    ILogger<CircuitBreakerChatClient> logger) : DelegatingChatClient
{
    public override async Task<ChatResponse> CompleteAsync(
        IList<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.IsHealthy(deploymentName))
        {
            logger.LogWarning("Circuit breaker open for '{Deployment}' — rejecting request", deploymentName);
            throw new InvalidOperationException($"Deployment '{deploymentName}' is currently unhealthy.");
        }

        try
        {
            var response = await base.CompleteAsync(chatMessages, options, cancellationToken);
            catalog.ReportHealth(deploymentName, true);
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            catalog.ReportHealth(deploymentName, false);
            logger.LogWarning(ex, "Circuit breaker recording failure for '{Deployment}'", deploymentName);
            throw;
        }
    }

    public override async IAsyncEnumerable<StreamingChatMessage> CompleteStreamingAsync(
        IList<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!catalog.IsHealthy(deploymentName))
        {
            logger.LogWarning("Circuit breaker open for '{Deployment}' — rejecting streaming request", deploymentName);
            throw new InvalidOperationException($"Deployment '{deploymentName}' is currently unhealthy.");
        }

        await foreach (var chunk in base.CompleteStreamingAsync(chatMessages, options, cancellationToken))
        {
            yield return chunk;
        }
    }
}
```

**Step 4: Integrate circuit breaker into DI**

Modify deployment resolution to wrap each keyed IChatClient with CircuitBreakerChatClient. This can be done by adding a `UseCircuitBreaker` extension or by wrapping during registration in `InfrastructureServiceExtensions`.

**Step 5: Write tests**

Test file: `Blaze.LlmGateway.Tests/Catalog/HealthProbeServiceTests.cs`

Test cases:
- Probe marks unhealthy deployment as unhealthy
- Healthy probe keeps healthy state
- CircuitBreakerChatClient rejects requests when unhealthy
- CircuitBreakerChatClient reports success on 200
- CircuitBreakerChatClient reports failure on exception

**Step 6: Run all tests**

Run: `dotnet test Blaze.LlmGateway.Tests -v`
Expected: 500+ passing (all existing + new)

**Step 7: Commit**

```bash
git add -A
git commit -m "feat: health probes, circuit breaker, fallback chain"
```

---

## Completion

After Phase 3, the provider catalog routing engine is fully integrated:

1. Config-driven deployments and routes → `InMemoryProviderCatalog` on startup
2. Routing strategies select the best deployment per request
3. `CatalogModelRouter` resolves virtual models through the catalog
4. `HealthProbeService` keeps health states current
5. `CircuitBreakerChatClient` short-circuits unhealthy deployments
6. Fallback chain catches degraded scenarios
