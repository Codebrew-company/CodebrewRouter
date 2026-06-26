# Brew — Deployment Checklist

## Prerequisites
- jarvis_ai running on port 8765 (WebSocket endpoint at `ws://jarvis.local:8765/ws`)
- CodebrewRouter running on port 5000 (OpenAI-compatible `/v1/chat/completions`)
- MEM0_API_KEY set in environment (for Brew.Api memory endpoints)

## Start

### 1. Brew.Api (backend)
```bash
cd /home/peterab/src/brew
MEM0_API_KEY=<your-key> dotnet run --project Brew.Api --urls http://localhost:5199
```

### 2. Brew.App (Blazor WASM frontend)
```bash
dotnet run --project Brew.App
```
This serves the Blazor app and accesses the API at the configured base URL.

## Test

### API Health
```bash
curl http://localhost:5199/health
# → {"status":"healthy"}
```

### Chat Relay
```bash
curl -X POST http://localhost:5199/api/chat \
  -H "Content-Type: application/json" \
  -d '{"messages":[{"role":"user","content":"hello"}]}'
# → 503 if router down; JSON response if router available
```

### Memory (requires MEM0_API_KEY)
```bash
curl http://localhost:5199/api/memory/all
# → paginated memory list from mem0
```

### Voice (requires browser)
1. Open browser to Blazor app URL (e.g., http://localhost:5000 or HTTPS equivalent)
2. Click the BrewRing
3. Allow microphone permission when prompted
4. Speak a test phrase — the ring transitions: Idle → Listening → Thinking → Speaking → Idle
5. Verify transcript appears in the reticle overlay
6. Verify TTS audio plays through browser speakers

## Known Limitations

### Voice Wiring Gap
The `BasicBrewLayout.razor` currently uses a demo `CycleRingState()` method that cycles through brew ring states with fixed delays instead of injecting and calling `VoiceService.ToggleRecording()`. The `VoiceService` is complete (all protocol handlers, state machine, JS interop, WebSocket flow) and registered in DI, but the layout/RingComponents needs to be connected:

- Inject `@inject VoiceService VoiceService` in the layout/page
- Replace `OnClick="CycleRingState"` with `OnClick="VoiceService.ToggleRecording"`
- Bind `State` to `VoiceService.State` instead of local `ringState`

### Blazor WASM Constraints
- ClientWebSocket buffer size: 256KB max per message (configured at 65536 bytes in VoiceService)
- Browser must be on same LAN as jarvis_ai (WebSocket connection from client browser)
- jarvis_ai must have Brew's origin in `extra_origin_hosts` for CORS/WebSocket acceptance

### Audio Pipeline
- Microphone capture via AudioWorklet at 16kHz mono PCM (Int16)
- TTS playback via Web Audio API AudioBufferSourceNode
- PCM processor at `/js/pcm-processor.js` (must be deployed alongside voiceCapture.js)

## Architecture Overview

```
Browser (Blazor WASM)
  ├── BrewRing (UI component)
  ├── VoiceService (WebSocket client)
  │     ├── JS → voiceCapture.js (mic capture)
  │     ├── JS → pcm-processor.js (AudioWorklet)
  │     └── WebSocket → jarvis_ai:8765/ws (PCM audio + protocol messages)
  ├── Brew.Api → CodebrewRouter:5000 (chat relay)
  └── Brew.Api → api.mem0.ai (memory persistence)
```

## Environment Variables

| Variable | Required | Default | Description |
|---|---|---|---|
| MEM0_API_KEY | For memory | — | mem0 platform API token |
| Voice:WebSocketUrl | For voice | ws://jarvis.local:8765/ws | jarvis_ai WebSocket endpoint |
| Voice:Token | Optional | "" | Auth token for jarvis_ai WS |
