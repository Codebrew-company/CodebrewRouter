using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Blaze.LlmGateway.Api;

public static class ProviderCertificateValidationPolicy
{
    public const string ConfigKey = "LlmGateway:AllowInvalidProviderCertificates";

    public static bool ShouldAllowInvalidProviderCertificates(
        IHostEnvironment environment,
        IConfiguration configuration)
        => environment.IsDevelopment()
           && configuration.GetValue<bool>(ConfigKey);
}
