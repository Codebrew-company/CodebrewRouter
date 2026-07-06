using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Brew.App.Components;
using Brew.App.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;
using Microsoft.JSInterop;

namespace Brew.App.Services;

/// <summary>
/// WebSocket client to jarvis_ai voice pipeline with full protocol handling,
/// state machine, and JS interop integration.
/// </summary>
public class VoiceService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private readonly ILogger<VoiceService> _logger;
    private readonly VoiceConfig _config;

    private IJSObjectReference? _jsModule;
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _receiveCts;
    private DotNetObjectReference<VoiceService>? _dotNetRef;
    private bool _disposed;

    // ─── Public properties ────────────────────────────────────────────

    public BrewRingState State { get; private set; } = BrewRingState.Idle;
    public string Transcript { get; private set; } = "";
    public string Response { get; private set; } = "";

    public event Action? StateChanged;
    public event Action<string>? TranscriptUpdated;
    public event Action<string>? ResponseUpdated;

    // ─── Constructor ──────────────────────────────────────────────────

    public VoiceService(IJSRuntime js, IOptions<VoiceConfig> config, ILogger<VoiceService> logger)
    {
        _js = js;
        _config = config.Value;
        _logger = logger;
    }

    // ─── Public methods ───────────────────────────────────────────────

    /// <summary>
    /// Toggle recording state: idle → start recording, listening → stop.
    /// Ignored while thinking or speaking.
    /// </summary>
    public async Task ToggleRecording()
    {
        if (_disposed) return;

        switch (State)
        {
            case BrewRingState.Idle:
                await StartRecording();
                break;
            case BrewRingState.Listening:
                await StopRecording();
                break;
            // Thinking / Speaking — ignore clicks, turn in progress
        }
    }

    /// <summary>
    /// Approve a dangerous command that jarvis_ai is requesting approval for.
    /// </summary>
    public async Task SendApproval(string runId)
    {
        if (_ws?.State == WebSocketState.Open)
        {
            var msg = JsonSerializer.Serialize(new { type = "approve", run_id = runId });
            await SendJson(msg);
        }
    }

    /// <summary>
    /// Deny a dangerous command that jarvis_ai is requesting approval for.
    /// </summary>
    public async Task SendDenial(string runId)
    {
        if (_ws?.State == WebSocketState.Open)
        {
            var msg = JsonSerializer.Serialize(new { type = "deny", run_id = runId });
            await SendJson(msg);
        }
    }

    // ─── JSInvokable callback ─────────────────────────────────────────

    /// <summary>
    /// Called by voiceCapture.js when a PCM audio chunk is available.
    /// Converts Int16 samples to bytes and forwards to the WebSocket.
    /// </summary>
    [JSInvokable]
    public async Task OnAudioChunk(int[] samples)
    {
        if (_ws?.State != WebSocketState.Open) return;

        try
        {
            // Convert int16 samples to little-endian bytes
            var bytes = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                var s = (short)Math.Clamp(samples[i], short.MinValue, short.MaxValue);
                bytes[i * 2] = (byte)(s & 0xFF);
                bytes[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
            }
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send audio chunk to jarvis_ai");
        }
    }

    // ─── Recording flow ───────────────────────────────────────────────

    private async Task StartRecording()
    {
        try
        {
            _dotNetRef = DotNetObjectReference.Create(this);

            // 1. Connect WebSocket to jarvis_ai
            await ConnectWebSocket();

            // 2. Send start message with audio format
            var startMsg = JsonSerializer.Serialize(new
            {
                type = "start",
                sample_rate = 16000,
                format = "pcm_s16le",
                channels = 1
            });
            await SendJson(startMsg);

            // 3. Start browser microphone capture via JS interop
            _jsModule = await _js.InvokeAsync<IJSObjectReference>("import", "/js/voiceCapture.js");
            await _jsModule.InvokeVoidAsync("startCapture", _dotNetRef, 16000);

            SetState(BrewRingState.Listening);
            _logger.LogInformation("Recording started — connected to jarvis_ai");
        }
        catch (JSException ex)
        {
            // Mic permission denied or other JS error
            _logger.LogWarning(ex, "JS interop failed (mic permission denied?)");
            await Cleanup();
            SetState(BrewRingState.Idle);
        }
        catch (WebSocketException ex)
        {
            _logger.LogError(ex, "WebSocket connection failed");
            await Cleanup();
            SetState(BrewRingState.Idle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording");
            await Cleanup();
            SetState(BrewRingState.Idle);
        }
    }

    private async Task StopRecording()
    {
        try
        {
            // 1. Stop browser capture
            if (_jsModule != null)
            {
                await _jsModule.InvokeVoidAsync("stopCapture");
            }

            // 2. Send stop message to jarvis_ai
            await SendJson(JsonSerializer.Serialize(new { type = "stop" }));

            // 3. Enter thinking state and start receive loop
            SetState(BrewRingState.Thinking);
            _receiveCts = new CancellationTokenSource();
            _ = ReceiveLoop(_receiveCts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop recording");
            await Cleanup();
            SetState(BrewRingState.Idle);
        }
    }

    // ─── WebSocket receive loop ───────────────────────────────────────

    private async Task ReceiveLoop(CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (_ws?.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    _logger.LogInformation("WebSocket closed by jarvis_ai");
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleTextMessage(json);
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    // TTS audio chunk from ElevenLabs
                    var pcm = new byte[result.Count];
                    Array.Copy(buffer, pcm, result.Count);
                    await HandleTtsAudio(pcm);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation on cleanup
        }
        catch (WebSocketException ex)
        {
            _logger.LogWarning(ex, "WebSocket disconnected mid-session");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Receive loop error");
        }
        finally
        {
            await Cleanup();
            if (State != BrewRingState.Idle)
                SetState(BrewRingState.Idle);
        }
    }

    // ─── Message handlers ─────────────────────────────────────────────

    private async Task HandleTextMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var type = typeProp.GetString();

            switch (type)
            {
                case "transcript":
                    HandleTranscript(root);
                    break;
                case "agent_status":
                    HandleAgentStatus(root);
                    break;
                case "done":
                    HandleDone(root);
                    break;
                case "error":
                    HandleError(root);
                    break;
                case "run_started":
                case "tool_call":
                case "approval_request":
                    // Log for observability; future: fire dedicated events
                    _logger.LogInformation("jarvis_ai: {Type} received", type);
                    break;
                default:
                    _logger.LogDebug("Unknown jarvis_ai message type: {Type}", type);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse jarvis_ai message: {Json}", json);
        }

        await Task.CompletedTask; // allows async override in subclasses
    }

    private void HandleTranscript(JsonElement msg)
    {
        var text = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var isPartial = msg.TryGetProperty("is_partial", out var p) && p.GetBoolean();

        Transcript = text;
        TranscriptUpdated?.Invoke(text);

        _logger.LogDebug("Transcript [{Partial}]: {Text}", isPartial ? "partial" : "final", text);
    }

    private void HandleAgentStatus(JsonElement msg)
    {
        var state = msg.TryGetProperty("state", out var s) ? s.GetString() : null;

        switch (state)
        {
            case "thinking":
                SetState(BrewRingState.Thinking);
                break;
            case "speaking":
                SetState(BrewRingState.Speaking);
                break;
            case "done":
                // Agent processing complete — "done" message type handles final cleanup
                break;
        }
    }

    private void HandleDone(JsonElement msg)
    {
        Response = msg.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        ResponseUpdated?.Invoke(Response);
        _logger.LogInformation("Turn complete. Response length: {Len}", Response.Length);
        // Cleanup happens in ReceiveLoop's finally block
    }

    private void HandleError(JsonElement msg)
    {
        var message = msg.TryGetProperty("message", out var m)
            ? m.GetString() ?? "Unknown error"
            : "Unknown error";
        _logger.LogError("jarvis_ai error: {Message}", message);
    }

    private async Task HandleTtsAudio(byte[] pcm)
    {
        try
        {
            if (_jsModule != null)
            {
                await _jsModule.InvokeVoidAsync("playPcm", pcm, 16000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to play TTS audio");
        }
    }

    // ─── WebSocket helpers ────────────────────────────────────────────

    private async Task ConnectWebSocket()
    {
        var url = _config.WebSocketUrl;
        if (!string.IsNullOrEmpty(_config.Token))
        {
            url += $"?token={Uri.EscapeDataString(_config.Token)}";
        }

        _ws = new ClientWebSocket();
        await _ws.ConnectAsync(new Uri(url), CancellationToken.None);
        _logger.LogInformation("Connected to jarvis_ai at {Url}", url);
    }

    private async Task SendJson(string json)
    {
        if (_ws?.State == WebSocketState.Open)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await _ws.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);
        }
    }

    // ─── State management ─────────────────────────────────────────────

    private void SetState(BrewRingState newState)
    {
        if (State != newState)
        {
            State = newState;
            StateChanged?.Invoke();
            _logger.LogDebug("State → {State}", newState);
        }
    }

    // ─── Cleanup ──────────────────────────────────────────────────────

    private async Task Cleanup()
    {
        // Cancel receive loop
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = null;

        // Stop JS capture and playback
        if (_jsModule != null)
        {
            try
            {
                await _jsModule.InvokeVoidAsync("stopCapture");
            }
            catch { /* May already be stopped */ }

            try
            {
                await _jsModule.InvokeVoidAsync("stopPlayback");
            }
            catch { /* May already be stopped */ }
        }

        // Close WebSocket
        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Brew session complete",
                    CancellationToken.None);
            }
            catch { /* Connection may already be closed */ }
        }
        _ws?.Dispose();
        _ws = null;

        // Release .NET reference
        _dotNetRef?.Dispose();
        _dotNetRef = null;

        _jsModule = null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await Cleanup();
        }
    }
}
