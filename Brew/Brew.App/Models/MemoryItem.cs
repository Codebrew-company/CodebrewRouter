namespace Brew.App.Models;

/// <summary>
/// A single memory entry from mem0 — displayed in the MemoryPanel.
/// </summary>
public class MemoryItem
{
    public string Id { get; set; } = string.Empty;
    public string Memory { get; set; } = string.Empty;
    public DateTime? CreatedAt { get; set; }
}
