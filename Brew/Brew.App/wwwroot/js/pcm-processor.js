/**
 * pcm-processor.js — AudioWorklet processor for microphone PCM capture.
 *
 * Registered name: 'pcm-processor'
 * Reads Float32 input from the first input channel, converts to Int16 PCM,
 * optionally resamples to a target sample rate, and posts the PCM data
 * back to the main thread via postMessage.
 *
 * Expected message from main thread:
 *   { type: 'config', targetSampleRate: 16000 }
 * If no config is sent, the processor passes through at the native sample rate.
 */

class PcmProcessor extends AudioWorkletProcessor {
  constructor(options) {
    super(options);
    // The native sample rate of the AudioContext.
    this.nativeSampleRate = sampleRate;
    // Target sample rate for downsampled output (default match native).
    this.targetSampleRate = sampleRate;
    // Accumulator for fractional resampling.
    this.resampleAccumulator = 0.0;

    // Listen for config messages from main thread.
    this.port.onmessage = (event) => {
      if (event.data && event.data.type === 'config') {
        if (event.data.targetSampleRate && event.data.targetSampleRate > 0) {
          this.targetSampleRate = Math.min(event.data.targetSampleRate, this.nativeSampleRate);
        }
      }
    };
  }

  /**
   * Convert Float32 samples in [-1.0, 1.0] to Int16 PCM in [-32768, 32767].
   */
  float32ToInt16(floatSamples) {
    const length = floatSamples.length;
    const int16 = new Int16Array(length);
    for (let i = 0; i < length; i++) {
      const s = Math.max(-1.0, Math.min(1.0, floatSamples[i]));
      int16[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
    }
    return int16;
  }

  /**
   * Simple linear-interpolation resampler.
   * Downsamples from native rate to target rate.
   * Returns an Int16Array of resampled PCM data.
   */
  resample(int16Data) {
    if (this.targetSampleRate >= this.nativeSampleRate) {
      // No downsampling needed — pass through.
      return int16Data;
    }

    const ratio = this.nativeSampleRate / this.targetSampleRate;
    const inputLength = int16Data.length;
    // Estimate output length, with a small safety margin.
    const outputLength = Math.ceil(inputLength / ratio) + 2;
    const output = new Int16Array(outputLength);

    let outIndex = 0;
    let frac = this.resampleAccumulator;

    while (frac < inputLength - 1 && outIndex < outputLength) {
      const idx = Math.floor(frac);
      const t = frac - idx;
      // Linear interpolation between adjacent samples.
      const interpolated = int16Data[idx] + (int16Data[idx + 1] - int16Data[idx]) * t;
      output[outIndex++] = Math.round(interpolated);
      frac += ratio;
    }

    // Save the fractional remainder for the next buffer.
    this.resampleAccumulator = frac - Math.floor(frac);

    // Return only the filled portion.
    return output.slice(0, outIndex);
  }

  process(inputs, outputs) {
    const input = inputs[0];
    if (!input || input.length === 0 || !input[0] || input[0].length === 0) {
      // No input — keep processor alive.
      return true;
    }

    const floatSamples = input[0];    // Float32Array of channel 0
    const int16Data = this.float32ToInt16(floatSamples);
    const resampled = this.resample(int16Data);

    // Post the Int16 PCM data to the main thread.
    this.port.postMessage(
      {
        type: 'pcm',
        sampleRate: this.targetSampleRate,
        data: resampled.buffer,
      },
      [resampled.buffer] // transfer ownership for zero-copy
    );

    // Return true to keep the processor alive.
    return true;
  }
}

registerProcessor('pcm-processor', PcmProcessor);
