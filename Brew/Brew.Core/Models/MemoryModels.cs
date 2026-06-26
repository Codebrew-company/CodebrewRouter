namespace Brew.Core.Models;

/// <summary>
/// Result returned when adding a memory.
/// </summary>
public class MemoryResult
{
    public string EventId { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// A single memory item retrieved from mem0.
/// </summary>
public class MemoryItem
{
    public string Id { get; set; } = string.Empty;
    public string Memory { get; set; } = string.Empty;
    public Dictionary<string, object>? Metadata { get; set; }
    public double? Score { get; set; }
    public DateTime? CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// Response from searching memories.
/// </summary>
public class MemorySearchResponse
{
    public List<MemoryItem> Results { get; set; } = new();
}

/// <summary>
/// Response from getting all memories (paginated).
/// </summary>
public class MemoryGetAllResponse
{
    public int Count { get; set; }
    public string? Next { get; set; }
    public string? Previous { get; set; }
    public List<MemoryItem> Results { get; set; } = new();
}

/// <summary>
/// Request body for adding a memory.
/// </summary>
public class AddMemoryRequest
{
    public string UserId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public List<MemoryMessage> Messages { get; set; } = new();
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// A single message in an AddMemory request.
/// </summary>
public class MemoryMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
}

/// <summary>
/// Request body for searching memories.
/// </summary>
public class SearchMemoryRequest
{
    public string UserId { get; set; } = string.Empty;
    public string AgentId { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public int TopK { get; set; } = 5;
}
