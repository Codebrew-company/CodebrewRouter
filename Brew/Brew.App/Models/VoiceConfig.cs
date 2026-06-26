namespace Brew.App.Models;

public class VoiceConfig
{
    public string WebSocketUrl { get; set; } = "ws://jarvis.local:8765/ws";
    public string Token { get; set; } = "";
}
