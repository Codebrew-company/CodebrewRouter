/**
 * voiceCapture.js — Browser microphone capture + TTS audio playback.
 *
 * This ES module provides functions for capturing microphone audio as Int16 PCM
 * via AudioWorklet, and playing back PCM audio through the Web Audio API.
 * Designed to be called from Blazor via IJSRuntime.
 *
 * Usage from Blazor (C#):
 *   var module = await JS.InvokeAsync<IJSObjectReference>("import", "/js/voiceCapture.js");
 *   await module.InvokeVoidAsync("startCapture", dotNetRef, 16000);
 */

// ─── Module-level state ────────────────────────────────────────────────

/** @type {MediaStream|null} */
let activeStream = null;

/** @type {AudioContext|null} */
let audioContext = null;

/** @type {AudioWorkletNode|null} */
let workletNode = null;

/** @type {MediaStreamSourceNode|null} */
let sourceNode = null;

/** @type {AudioBufferSourceNode[]} */
const activePlaybackNodes = [];

// ─── Helpers ────────────────────────────────────────────────────────────

/**
 * Create an AudioContext at the requested sample rate.
 * Falls back to the default rate if the browser doesn't support the requested rate.
 */
function createAudioContext(sampleRate) {
  // Modern browsers may support explicit sampleRate.
  const ctx = new AudioContext({ sampleRate });
  console.log(
    `[voiceCapture] AudioContext created. requested=${sampleRate} actual=${ctx.sampleRate}`
  );
  return ctx;
}

/**
 * Load the pcm-processor AudioWorklet module and create a worklet node.
 */
async function loadWorklet(ctx, dotNetRef) {
  // Add the worklet module.  Path is relative to the origin; in Blazor WASM
  // static files under wwwroot/js/ are served at /js/.
  await ctx.audioWorklet.addModule('/js/pcm-processor.js');

  // Create the node, connecting the worklet's message port.
  const node = new AudioWorkletNode(ctx, 'pcm-processor');

  // Forward PCM chunks to the Blazor .NET object.
  node.port.onmessage = (event) => {
    if (event.data && event.data.type === 'pcm') {
      const int16Data = new Int16Array(event.data.data);
      // Convert to a plain array so .NET can receive it via IJSRuntime.
      dotNetRef.invokeMethodAsync('OnAudioChunk', Array.from(int16Data));
    }
  };

  return node;
}

// ─── Exported functions ─────────────────────────────────────────────────

/**
 * Start microphone capture.
 *
 * @param {object}   dotNetRef  - .NET object reference to invoke callbacks on.
 * @param {number}   sampleRate - Target sample rate in Hz (default 16000).
 * @returns {Promise<void>}
 */
export async function startCapture(dotNetRef, sampleRate = 16000) {
  if (activeStream) {
    console.warn('[voiceCapture] Capture already active. Stopping previous.');
    stopCapture();
  }

  // 1. Request microphone access.
  const stream = await navigator.mediaDevices.getUserMedia({
    audio: {
      sampleRate,
      channelCount: 1,
      echoCancellation: true,
      noiseSuppression: true,
      autoGainControl: true,
    },
  });
  activeStream = stream;
  console.log(`[voiceCapture] getUserMedia succeeded.`);

  // 2. Create AudioContext and load worklet.
  audioContext = createAudioContext(sampleRate);
  workletNode = await loadWorklet(audioContext, dotNetRef);

  // 3. Configure the worklet with the target sample rate.
  workletNode.port.postMessage({
    type: 'config',
    targetSampleRate: audioContext.sampleRate > sampleRate ? sampleRate : audioContext.sampleRate,
  });

  // 4. Connect the graph: mic → source → worklet → (no output; PCM is posted via port).
  sourceNode = audioContext.createMediaStreamSource(stream);
  sourceNode.connect(workletNode);
  // We do NOT connect workletNode to audioContext.destination —
  // the worklet captures in-process and posts data, no audio output needed.
}

/**
 * Stop microphone capture and clean up all audio resources.
 */
export function stopCapture() {
  // Kill active playback nodes first.
  stopPlayback();

  if (workletNode) {
    workletNode.port.onmessage = null;
    workletNode.disconnect();
    workletNode = null;
  }

  if (sourceNode) {
    sourceNode.disconnect();
    sourceNode = null;
  }

  if (activeStream) {
    activeStream.getTracks().forEach((track) => track.stop());
    activeStream = null;
  }

  if (audioContext) {
    audioContext.close();
    audioContext = null;
  }

  console.log('[voiceCapture] Capture stopped.');
}

/**
 * Play raw Int16 PCM audio bytes through the default audio output.
 *
 * @param {Uint8Array|number[]} pcmBytes  - Raw Int16 PCM data (little-endian byte pairs).
 * @param {number}             sampleRate - Sample rate of the PCM data in Hz (default 16000).
 * @returns {Promise<void>}
 */
export async function playPcm(pcmBytes, sampleRate = 16000) {
  // Ensure we have an AudioContext for playback.
  if (!audioContext || audioContext.state === 'closed') {
    audioContext = createAudioContext(sampleRate);
  }

  // Convert byte array → Int16Array → Float32Array.
  let int16;
  if (pcmBytes instanceof Uint8Array) {
    int16 = new Int16Array(pcmBytes.buffer, pcmBytes.byteOffset, pcmBytes.byteLength / 2);
  } else if (Array.isArray(pcmBytes)) {
    int16 = new Int16Array(pcmBytes);
  } else if (pcmBytes instanceof Int16Array) {
    int16 = pcmBytes;
  } else {
    throw new Error('[voiceCapture] playPcm: unsupported input type');
  }

  const frameCount = int16.length;
  const float32 = new Float32Array(frameCount);
  for (let i = 0; i < frameCount; i++) {
    float32[i] = int16[i] / (int16[i] < 0 ? 0x8000 : 0x7FFF);
  }

  // Create an AudioBuffer and source node.
  const buffer = audioContext.createBuffer(1, frameCount, sampleRate);
  buffer.getChannelData(0).set(float32);

  const source = audioContext.createBufferSource();
  source.buffer = buffer;
  source.connect(audioContext.destination);

  // Track this node so stopPlayback can cancel it.
  activePlaybackNodes.push(source);

  source.onended = () => {
    const idx = activePlaybackNodes.indexOf(source);
    if (idx >= 0) activePlaybackNodes.splice(idx, 1);
  };

  source.start();
  console.log(`[voiceCapture] Playing ${frameCount} frames at ${sampleRate} Hz.`);
}

/**
 * Immediately stop all active PCM playback.
 */
export function stopPlayback() {
  while (activePlaybackNodes.length > 0) {
    const node = activePlaybackNodes.pop();
    try {
      node.onended = null;
      node.stop();
    } catch (_) {
      // Ignore — node may have already stopped.
    }
  }
  console.log('[voiceCapture] All playback stopped.');
}
