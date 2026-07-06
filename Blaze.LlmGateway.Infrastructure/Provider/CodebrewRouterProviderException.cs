namespace Blaze.LlmGateway.Infrastructure.Provider;

/// <summary>
/// Base exception for all CodebrewRouterProvider errors.
/// </summary>
public class CodebrewRouterProviderException : InvalidOperationException
{
    public CodebrewRouterProviderException(string message) : base(message) { }
    public CodebrewRouterProviderException(string message, Exception innerException) 
        : base(message, innerException) { }
}

/// <summary>
/// Thrown when provider validation fails (configuration, connectivity, dependencies).
/// </summary>
public class CodebrewRouterProviderValidationException : CodebrewRouterProviderException
{
    public List<ValidationError> ValidationErrors { get; }

    public CodebrewRouterProviderValidationException(
        string message,
        List<ValidationError> validationErrors) : base(message)
    {
        ValidationErrors = validationErrors ?? [];
    }

    public override string ToString()
    {
        var errors = string.Join("\n  - ", ValidationErrors.Select(e => $"{e.Code}: {e.Message}"));
        return $"{base.ToString()}\n\nValidation Errors:\n  - {errors}";
    }
}

/// <summary>
/// Thrown when provider initialization fails (services can't start).
/// </summary>
public class CodebrewRouterProviderInitializationException : CodebrewRouterProviderException
{
    public CodebrewRouterProviderInitializationException(string message) : base(message) { }
    public CodebrewRouterProviderInitializationException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>
/// A single validation error with code, message, and optional recommendation.
/// </summary>
public record ValidationError(
    string Code,
    string Message,
    string? Recommendation = null);
