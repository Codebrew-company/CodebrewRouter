using OllamaSharp;
using System;
using System.Reflection;

namespace Blaze.LlmGateway.Infrastructure;

/// <summary>
/// Extension to create OllamaApiClient with custom timeout.
/// OllamaSharp 5.4.25 doesn't expose timeout configuration, so we use reflection
/// to set the HttpClient's timeout after instantiation.
/// </summary>
public static class OllamaClientExtensions
{
    /// <summary>
    /// Create an OllamaApiClient with a custom timeout for large model inference.
    /// This is needed because .12 may be running 26B models that take 30+ seconds per inference.
    /// </summary>
    public static OllamaApiClient CreateWithTimeout(
        Uri baseUri,
        string model,
        TimeSpan timeout)
    {
        var client = new OllamaApiClient(baseUri, model);
        
        try
        {
            // OllamaApiClient has a private _httpClient field
            var httpClientField = typeof(OllamaApiClient).GetField("_httpClient", 
                BindingFlags.NonPublic | BindingFlags.Instance);
            
            if (httpClientField?.GetValue(client) is HttpClient httpClient)
            {
                httpClient.Timeout = timeout;
            }
        }
        catch
        {
            // If reflection fails, just use the default client
            // (The old timeout limitation will still apply, but at least it won't crash)
        }
        
        return client;
    }
}
