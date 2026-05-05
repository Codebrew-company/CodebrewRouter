# Migration: LocalInferenceServices → CodebrewRouterProvider

## Overview
The `AddLocalInferenceServices(IConfiguration)` API is deprecated in favor of the new `AddCodebrewRouterProvider(CodebrewRouterProviderOptions)` API.

**Status:** Existing code continues to work (backwards compatible). Plan migration at your convenience.

## Why Migrate?
- **Mobile-ready:** IOptions pattern works on MAUI without appsettings.json
- **Explicit:** No hidden IConfiguration binding; all options visible in code
- **Composable:** Fluent builder for opt-in features
- **Testable:** No mock IConfiguration needed

## Migration Steps

### Before (deprecated)
```csharp
builder.Services.AddLocalInferenceServices(configuration);
```

### After (new)
```csharp
var options = new CodebrewRouterProviderOptions
{
    LocalEndpoint = "http://localhost:11434",
    RemoteDiscoveryEndpoint = "http://localhost:5273",
    CacheAvailabilityTtlSeconds = 60,
    DiscoveryPollingIntervalSeconds = 300
};

builder.Services
    .AddCodebrewRouterProvider(options)
    .WithHealthChecks()
    .WithDiscovery()
    .WithRouting()
    .Build();
```

### Or from Configuration
```csharp
var section = configuration.GetSection("LlmGateway:LocalInference");
var options = new CodebrewRouterProviderOptions();
section.Bind(options);

builder.Services.AddCodebrewRouterProvider(options);
```

## Timeline
- **v1.x:** Both APIs work. New API preferred. Old API marked [Obsolete].
- **v2.0:** Old API removed.
