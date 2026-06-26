namespace Brew.Core.Models;

public class ChatMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

public class ChatRequest
{
    public string Model { get; set; } = "brew";
    public List<ChatMessage> Messages { get; set; } = new();
    public bool Stream { get; set; } = false;
    public float? Temperature { get; set; }
}

public class ChatResponse
{
    public string Content { get; set; } = "";
    public string Model { get; set; } = "";
    public int TokensUsed { get; set; }
    public string FinishReason { get; set; } = "";
}
