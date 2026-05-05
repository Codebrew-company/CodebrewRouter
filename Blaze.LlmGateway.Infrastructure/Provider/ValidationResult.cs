namespace Blaze.LlmGateway.Infrastructure.Provider;

/// <summary>
/// Result of provider configuration validation.
/// Contains errors (if any) and metadata about validation run.
/// </summary>
public class ValidationResult
{
    /// <summary>
    /// Validation passed (no errors).
    /// </summary>
    public bool IsValid { get; }

    /// <summary>
    /// Collection of validation errors (empty if valid).
    /// </summary>
    public List<ValidationError> Errors { get; }

    /// <summary>
    /// Timestamp when validation ran.
    /// </summary>
    public DateTime ValidatedAt { get; }

    public ValidationResult(bool isValid, List<ValidationError> errors)
    {
        IsValid = isValid;
        Errors = errors ?? [];
        ValidatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Create a successful validation result.
    /// </summary>
    public static ValidationResult Success() => new(true, []);

    /// <summary>
    /// Create a failed validation result with errors.
    /// </summary>
    public static ValidationResult Failure(params ValidationError[] errors)
        => new(false, errors.ToList());

    /// <summary>
    /// Create a failed validation result with errors and a message.
    /// </summary>
    public static ValidationResult Failure(string message, params ValidationError[] errors)
    {
        var allErrors = new List<ValidationError> { new("GENERAL", message) };
        allErrors.AddRange(errors);
        return new(false, allErrors);
    }

    public override string ToString()
    {
        if (IsValid) return "Validation: PASSED";
        var errorList = string.Join("\n  - ", Errors.Select(e => $"{e.Code}: {e.Message}"));
        return $"Validation: FAILED\n  - {errorList}";
    }
}
