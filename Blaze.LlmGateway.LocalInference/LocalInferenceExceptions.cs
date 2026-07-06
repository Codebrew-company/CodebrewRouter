namespace Blaze.LlmGateway.LocalInference;

/// <summary>
/// Thrown when a local model is not available for use.
/// </summary>
public class LocalModelUnavailableException : InvalidOperationException
{
    public LocalModelUnavailableException(string? message) : base(message)
    {
    }

    public LocalModelUnavailableException(string? message, Exception? innerException) 
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when remote model discovery fails and cannot be recovered.
/// </summary>
public class RemoteDiscoveryFailedException : InvalidOperationException
{
    public RemoteDiscoveryFailedException(string? message) : base(message)
    {
    }

    public RemoteDiscoveryFailedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Thrown when health check fails due to both local and remote availability being unavailable.
/// </summary>
public class HealthCheckFailedException : InvalidOperationException
{
    public HealthCheckFailedException(string? message) : base(message)
    {
    }

    public HealthCheckFailedException(string? message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
