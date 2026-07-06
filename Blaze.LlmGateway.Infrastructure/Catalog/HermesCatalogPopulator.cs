using System.Collections.Generic;
using System.Linq;
using Blaze.LlmGateway.Core.Catalog;
using Blaze.LlmGateway.Core.Configuration;

namespace Blaze.LlmGateway.Infrastructure.Catalog;

/// <summary>
/// Generates ProviderDeployment entries for each enabled Hermes profile
/// from <see cref="HermesProviderOptions"/>.
/// These can be fed into <see cref="InMemoryProviderCatalog"/> at startup
/// or into a dynamic catalog extension point.
/// </summary>
public static class HermesCatalogPopulator
{
    public static IReadOnlyList<ProviderDeployment> GenerateDeployments(
        HermesProviderOptions hermesOpts)
    {
        var deployments = new List<ProviderDeployment>();
        foreach (var (profileName, profileOpts) in hermesOpts.Profiles)
        {
            if (!profileOpts.Enabled) continue;

            var modelName = $"hermes-{profileName}"; // e.g. "hermes-derp-coder"
            var deploymentName = $"hermes-{profileName}-gw";
            var providerKey = $"Hermes_{ToPascalCase(profileName)}";
            
            // Resolve endpoint override
            var endpoint = profileOpts.Endpoint;
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                var host = profileOpts.Host ?? hermesOpts.Host;
                endpoint = $"http://{host}:{profileOpts.Port}/v1";
            }

            var capabilities = profileOpts.Capabilities;
            if (capabilities == null || capabilities.Length == 0)
            {
                capabilities = ["chat", "tools", "streaming"];
            }

            var tags = profileOpts.Tags;
            if (tags == null || tags.Length == 0)
            {
                tags = ["hermes", "local"];
            }

            deployments.Add(new ProviderDeployment
            {
                Name = deploymentName,
                ModelName = modelName,
                Provider = providerKey,
                Endpoint = endpoint,
                ApiKey = profileOpts.ApiKey ?? hermesOpts.ApiKey,
                Model = profileOpts.Model, // Pass the custom OpenAI model ID if configured
                Weight = 1,
                Priority = 10,
                MaxContextTokens = profileOpts.MaxContextTokens ?? hermesOpts.MaxContextTokens,
                Capabilities = capabilities,
                Tags = tags,
                MaxRequestsPerMinute = profileOpts.MaxRequestsPerMinute,
                MaxTokensPerMinute = profileOpts.MaxTokensPerMinute,
                CostPerToken = 0,
                Enabled = true
            });
        }
        return deployments;
    }

    private static string ToPascalCase(string profileName)
    {
        // "derp-coder" → "DerpCoder", "default" → "Default"
        return string.Concat(profileName.Split('-', '_')
            .Select(static word => word.Length > 0
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                : ""));
    }
}
