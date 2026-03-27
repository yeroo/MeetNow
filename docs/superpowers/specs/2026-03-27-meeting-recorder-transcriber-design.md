# Meeting Recorder & Transcriber — Design Spec

**Date:** 2026-03-27
**Status:** Approved
**Scope:** MVP — capture + transcribe + store (no knowledge extraction)

## Overview

A fully local Windows 11 pipeline for capturing and transcribing work meetings. Continuously monitors system audio output via WASAPI loopback, uses VAD to detect human speech, records speech segments as time-aligned dual-channel chunks (loopback + microphone), and transcribes each chunk locally using Faster-Whisper on GPU.

The system does not attempt to detect whether a "meeting" is happening. It records all detected speech from the sound card, splits it into chunks based on silence boundaries, and transcribes everything. The result is a personal local knowledge base of meeting conversations organized by session.

## Architecture: File-Drop Pipeline

Two independent processes communicating via the filesystem:

1. **C# Recorder** — captures audio, runs VAD, writes WAV chunks + metadata JSON to a watched directory
2. **Python Transcriber** — polls the directory for pending chunks, transcribes with Faster-Whisper, writes transcript JSON alongside them

The filesystem is the contract. Either process can crash and restart independently. The C# recorder knows nothing about Python. The Python transcriber knows nothing about C#.

## Solution Layout

```
MeetNow.sln
  MeetNow/                              ← existing WPF app (untouched)
  TestCacheReader/                       ← existing utility (untouched)
  MeetNow.Recording.Core/               ← reusable .NET class library
  MeetNow.Recording.Contracts/           ← shared schemas/models (no dependencies)
  MeetNow.Recorder/                      ← standalone console executable (thin host)
  MeetNow.Recorder.Transcriber/          ← Python transcription package
  MeetNow.Recording.Core.Tests/          ← xUnit test project
```

### MeetNow.Recording.Contracts

Target: `net8.0` (no -windows, maximally portable). Zero external dependencies — POCOs with `System.Text.Json` attributes.

```
MeetNow.Recording.Contracts/
  ChunkMetadata.cs                        ← chunk JSON schema
  SessionMetadata.cs                      ← session JSON schema
  TranscriptResult.cs                     ← transcript output schema
  TranscriptSegment.cs                    ← segment with word-level timestamps
  MergedTranscript.cs                     ← session_transcript.json schema
  ChunkStatus.cs                          ← enum: PendingTranscription, Transcribed, Archived
  VadStats.cs                             ← speech frame counts
  SplitReason.cs                          ← enum: SilenceTimeout, MaxDuration, DeviceChange, ManualStop
```

Both C# projects reference this. The Python transcriber reads/writes the same JSON shapes.

### MeetNow.Recording.Core

Target: `net8.0-windows10.0.19041.0`. Dependencies: NAudio, WebRtcVadSharp.

```
MeetNow.Recording.Core/
  Audio/
    DualChannelCapture.cs                ← orchestrates loopback + mic streams
    LoopbackCaptureStream.cs             ← WasapiLoopbackCapture wrapper, resamples to 16kHz mono
    MicrophoneCaptureStream.cs           ← WasapiCapture wrapper, resamples to 16kHz mono
    RingBuffer.cs                        ← circular PCM buffer (pre-buffer)
    AudioFormat.cs                       ← constants: 16kHz, mono, 16-bit
  Vad/
    IVoiceActivityDetector.cs            ← interface: bool IsSpeech(ReadOnlySpan<short> frame)
    WebRtcVoiceActivityDetector.cs       ← WebRTC VAD implementation
  Recording/
    RecordingStateMachine.cs             ← IDLE/RECORDING/DRAINING/FLUSHING states
    ChunkTimeline.cs                     ← synchronized clock for both channels
    ChunkWriter.cs                       ← writes WAV files + chunk metadata JSON
    SessionManager.cs                    ← creates session folders, detects session gaps
  Config/
    RecorderConfig.cs                    ← all configurable parameters with defaults
  DeviceMonitor.cs                       ← watches for audio device changes
```

### MeetNow.Recorder

Target: `net8.0-windows10.0.19041.0`, `win-x64`, self-contained, single-file.

```
MeetNow.Recorder/
  Program.cs                             ← thin entry point: config + cancellation + RunAsync
  RecorderService.cs                     ← orchestrates capture + Python process lifecycle
  TranscriberProcessManager.cs           ← starts/monitors/restarts Python process
  appsettings.json                       ← all configuration
```

`Program.cs` is thin — just wiring:

```csharp
var config = RecorderConfig.Load("appsettings.json");
var service = new RecorderService(config);
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
await service.RunAsync(cts.Token);
```

### MeetNow.Recorder.Transcriber

```
MeetNow.Recorder.Transcriber/
  transcriber/
    __init__.py
    __main__.py                          ← entry point: python -m transcriber
    watcher.py                           ← polls for pending chunks
    transcribe.py                        ← Faster-Whisper transcription logic
    merger.py                            ← builds session_transcript.json
    config.py                            ← CLI args and defaults
  requirements.txt                       ← faster-whisper
```

### MeetNow.Recording.Core.Tests

```
MeetNow.Recording.Core.Tests/
  Vad/
    RecordingStateMachineTests.cs         ← state transitions, hysteresis, edge cases
    ChunkTimelineTests.cs                 ← synchronized timing across channels
  Recording/
    ChunkWriterTests.cs                   ← WAV output correctness, metadata alignment
    SessionManagerTests.cs                ← session gap detection, folder creation
    RingBufferTests.cs                    ← circular buffer overflow, drain behavior
  DeviceMonitorTests.cs                   ← device switch simulation
```

Priority: state machine > ring buffer > chunk timeline sync > session gap detection.

## Audio Capture Architecture

### Dual-Channel WASAPI Capture

Two independent capture streams:

- **Loopback** — `WasapiLoopbackCapture` on default render device. Captures all system audio (all meeting participants).
- **Microphone** — `WasapiCapture` on default capture device. Captures the user's voice only.

Both streams are resampled and converted at capture time to a common format: **16kHz mono 16-bit PCM**. This is Whisper's native input format. No conversion needed before transcription.

At 16kHz mono 16-bit, each channel produces ~115 MB/hour (~32 KB/s).

### No Audio Isolation

The system does NOT attempt to isolate Teams audio from other system sounds. Reasons:
- Windows doesn't expose per-app audio streams to WASAPI loopback
- VAD handles filtering — non-speech sounds are rejected by the VAD, not by audio routing
- No virtual audio cable driver needed, no IT concerns

### Rolling Pre-Buffer

Both streams continuously write into a **5-second circular ring buffer** of PCM samples. When VAD triggers speech start, the buffer contents are prepended to the chunk. This ensures the first spoken words are not clipped — speech onset typically arrives 200-500ms before VAD detects it.

### Device Switching

Audio device changes (e.g., Bluetooth headset connects/disconnects) are detected via `IMMNotificationClient` callback from `MMDeviceEnumerator`. On device change:
1. Current chunk is flushed with `splitReason: DeviceChange`
2. Capture streams are torn down and re-initialized on new default devices
3. New chunk begins on new devices
4. Session continues (same session, just a chunk split)

## VAD and Speech-Triggered Recording

### VAD Engine

WebRTC VAD via **WebRtcVadSharp** NuGet package. Classifies 30ms audio frames (480 samples at 16kHz) as speech or non-speech.

Accessed through `IVoiceActivityDetector` interface to allow future replacement with Silero VAD (ONNX Runtime).

### VAD Channel

**Loopback only** drives the VAD. The mic channel records in lockstep but does not independently trigger recording. Rationale:
- Meeting conversations always produce loopback audio
- Mic-only VAD would trigger on phone calls, hallway conversations, self-talk
- Future: configurable toggle to allow mic-triggered recording

### Speech-Start Hysteresis

IDLE→RECORDING requires **3 positive VAD frames within a 5-frame window** (150ms), not a single frame. This prevents isolated noise spikes from opening a chunk.

### Mic Keepalive

During RECORDING, if loopback goes silent but mic VAD detects speech, the chunk stays open for up to **10 seconds**. This covers the user responding to a question while the other side is quiet. After 10s of loopback silence, the chunk closes regardless.

### State Machine

```
IDLE ──[hysteresis met on loopback]──► RECORDING ──[silence on BOTH]──► DRAINING ──[3s timeout]──► FLUSHING ──► IDLE
                                           │              ▲                    │
                                           │              └──[speech resumes]──┘
                                           └──[max duration]──► FLUSHING ──► IDLE
```

- **IDLE:** VAD runs on every loopback frame. Audio goes into ring buffer (both channels). Nothing written to disk.
- **RECORDING:** Hysteresis met. Ring buffer drained into new chunk (both channels). All subsequent frames appended. Stays in RECORDING as long as speech frames arrive on either channel (with mic keepalive rules).
- **DRAINING:** Loopback silence detected and mic keepalive window (10s) has elapsed without mic speech (or mic was already silent). Waiting for silence timeout. If speech resumes on loopback within timeout, back to RECORDING (same chunk).
- **FLUSHING:** Silence timeout elapsed OR max chunk duration reached. Close WAV files for both channels. Write chunk metadata JSON. Update session metadata. Return to IDLE.

### Max Chunk Duration Split

When a chunk hits max duration, wait for the next silence gap (300ms+ pause) within a 30-second grace window. If no pause arrives in 30s, hard-cut. The next chunk starts with a fresh pre-buffer to ensure overlap and no lost words.

### Configurable Parameters

| Parameter | Default | Notes |
|---|---|---|
| VAD aggressiveness | 3 | Configurable 0-3. Drop to 2 if miss rate is too high |
| VAD frame size | 30ms | 480 samples at 16kHz |
| Speech-start hysteresis | 3 of 5 frames | 3 positive frames within 150ms window |
| Silence timeout | 3 seconds | Bridges natural pauses |
| Pre-buffer | 5 seconds | Captures speech onset |
| Min chunk duration | 2 seconds | Discard accidental blips |
| Max chunk duration | 5 minutes | Force-split long monologues |
| Max chunk grace window | 30 seconds | Hard-cut if no pause found |
| Mic keepalive window | 10 seconds | Mic speech extends open chunk |
| Session gap | 10 minutes | Continuous silence before new session |

## File Layout and Metadata

### Output Directory Structure

Base: `%LOCALAPPDATA%\MeetNow\Recordings` (configurable).

```
Recordings/
  2026-03-27_14-32-05/                   ← session folder
    session.json                          ← session metadata
    chunks/
      chunk_001_loopback.wav
      chunk_001_mic.wav
      chunk_001.json                      ← chunk metadata
      chunk_002_loopback.wav
      chunk_002_mic.wav
      chunk_002.json
    transcripts/
      chunk_001_loopback.json             ← Whisper output
      chunk_001_mic.json
      chunk_002_loopback.json
      chunk_002_mic.json
    session_transcript.json               ← merged timeline
```

### Session Boundaries

A new session starts when:
- The recorder starts for the first time
- More than 10 minutes of continuous silence pass between chunks

### Chunk Metadata (`chunk_001.json`)

```json
{
  "chunkIndex": 1,
  "sessionId": "2026-03-27_14-32-05",
  "startTimeUtc": "2026-03-27T14:32:05.123Z",
  "endTimeUtc": "2026-03-27T14:32:47.891Z",
  "durationSeconds": 42.768,
  "loopbackFile": "chunk_001_loopback.wav",
  "micFile": "chunk_001_mic.wav",
  "vadStats": {
    "speechFrames": 1287,
    "totalFrames": 1426,
    "speechRatio": 0.903
  },
  "splitReason": "silence_timeout",
  "status": "pending_transcription"
}
```

### Session Metadata (`session.json`)

```json
{
  "sessionId": "2026-03-27_14-32-05",
  "startTimeUtc": "2026-03-27T14:32:05.123Z",
  "endTimeUtc": null,
  "chunkCount": 0,
  "status": "recording",
  "captureDevices": {
    "loopback": "Speakers (Realtek High Definition Audio)",
    "microphone": "Microphone Array (Intel Smart Sound)"
  },
  "config": {
    "vadMode": 3,
    "silenceTimeoutMs": 3000,
    "maxChunkDurationMs": 300000
  }
}
```

### Audio Format Choice

**WAV** for intermediate storage:
- Whisper reads WAV natively with zero conversion overhead
- No encoding CPU cost during recording
- 5-minute chunk at 16kHz mono 16-bit = ~9.2 MB

**FLAC** for archival (post-transcription, optional):
- Lossless, ~60% smaller than WAV
- Chunk metadata updated to point to .flac files
- Original WAVs deleted

## Python Transcription Pipeline

### Process Lifecycle

A single long-running Python process started and monitored by the C# recorder:

```csharp
var psi = new ProcessStartInfo
{
    FileName = "python",
    Arguments = "-m transcriber --model small --watch-dir \"<base_dir>\" --device cuda",
    UseShellExecute = false,
    RedirectStandardError = true,
    CreateNoWindow = true
};
```

C# monitors the process handle. If it exits unexpectedly, restart with backoff. On recorder shutdown, signal stop (write `stop.flag`) and wait up to 5s for current transcription to finish, then kill.

### Model Selection

Default: **small** (~2 GB VRAM, ~2s per 30s chunk). Configurable.

| Model | VRAM | Speed (30s) | Accuracy |
|---|---|---|---|
| base | ~1 GB | ~1s | Moderate |
| small | ~2 GB | ~2s | Good (MVP default) |
| medium | ~5 GB | ~4s | Very good |
| large-v3 | ~10 GB | ~8s | Best |

### Watcher Loop

Simple polling (no `watchdog` dependency):

```python
while running:
    for session_dir in base_dir.iterdir():
        for chunk_json in (session_dir / "chunks").glob("chunk_*.json"):
            meta = json.loads(chunk_json.read_text())
            if meta["status"] == "pending_transcription":
                transcribe(chunk_json, meta)
    time.sleep(2)
```

Polling is resilient to filesystem race conditions and automatically picks up missed chunks on restart (crash recovery for free).

### Transcript Output (`chunk_001_loopback.json`)

```json
{
  "chunkIndex": 1,
  "channel": "loopback",
  "language": "en",
  "languageProbability": 0.97,
  "segments": [
    {
      "start": 0.0,
      "end": 3.42,
      "text": "So the main issue with the deployment pipeline is",
      "words": [
        {"word": "So", "start": 0.0, "end": 0.18, "probability": 0.94},
        {"word": "the", "start": 0.18, "end": 0.28, "probability": 0.98}
      ]
    }
  ],
  "transcriptionTimeSeconds": 1.8,
  "modelName": "small",
  "timestampUtcBase": "2026-03-27T14:32:05.123Z"
}
```

Word-level timestamps included — free from Faster-Whisper and critical for future speaker alignment.

### Merged Session Transcript

Built after a session ends (10-minute gap or recorder shutdown):

```json
{
  "sessionId": "2026-03-27_14-32-05",
  "duration": "00:47:23",
  "channels": {
    "loopback": [
      {"start": "14:32:05", "end": "14:32:48", "text": "So the main issue with..."}
    ],
    "mic": [
      {"start": "14:32:48", "end": "14:33:10", "text": "I think we should move..."}
    ]
  },
  "merged": [
    {"start": "14:32:05", "speaker": "other", "text": "So the main issue with..."},
    {"start": "14:32:48", "speaker": "me", "text": "I think we should move..."},
    {"start": "14:33:12", "speaker": "other", "text": "Right, and we also need to..."}
  ]
}
```

The `merged` view interleaves both channels by timestamp with `"me"` vs `"other"` labels.

### Crash Recovery

| Scenario | Recovery |
|---|---|
| C# crashes mid-chunk | WAV may be truncated. Python handles short WAVs. Chunk JSON may be missing — Python skips orphan WAVs. |
| C# crashes between chunks | Completed chunks on disk with `pending_transcription`. Python transcribes on next poll. |
| Python crashes mid-transcription | Status stays `pending_transcription`. Python retries on restart. Partial transcripts overwritten. |
| Both crash | On restart, C# starts new session. Python picks up all pending chunks from all sessions. |

## Reliability and Edge Cases

### Overlapping Speech

Handled naturally by dual-channel design:
- Loopback captures mixed audio — Whisper handles overlapping speech reasonably well
- Mic captures user's voice separately — clean transcript even if loopback is noisy

### Teams Audio Enhancements

Teams applies noise suppression, echo cancellation, and AGC before output. This helps — the loopback stream is already cleaned up. Caveat: Teams may suppress background voices in shared-room scenarios.

### False Triggers

Mitigations:
- VAD mode 3 rejects most non-speech
- Hysteresis (3/5 frames) prevents isolated spikes
- Min chunk duration (2s) discards blips
- Future: Silero VAD upgrade for better speech vs media discrimination

### Disk Space

- ~230 MB/hour raw (both channels), ~90 MB/hour FLAC
- ~4 hours of speech per 8-hour day: ~360 MB/day raw, ~140 MB/day compressed
- Monitor free space every 10 minutes. Warning at <1 GB, stop recording at <500 MB.

### Startup Sequence

1. Load config from `appsettings.json`
2. Enumerate audio devices
3. Initialize capture streams (paused)
4. Start Python transcriber process
5. Unpause capture — begin VAD monitoring

### Shutdown Sequence

1. CancellationToken fires
2. Flush current chunk (both channels) with `splitReason: ManualStop`
3. Finalize session JSON (`endTimeUtc`, final `chunkCount`)
4. Signal Python to stop, wait up to 5s, then kill
5. Dispose capture streams

## Privacy and Policy

- **Local-only processing.** No network calls. No cloud APIs. No telemetry.
- **Visible operation.** Console window shows state and activity. No stealth mode.
- **User responsibility.** The user is responsible for compliance with their organization's meeting recording policies. The tool records system audio output and provides no mechanism to hide its operation.
- **No automatic deletion in MVP.** User manages their own recordings folder.
- **Future:** Configurable retention policy (delete sessions older than N days, keep transcripts but delete audio after M days).

## Configuration Reference (`appsettings.json`)

```json
{
  "recording": {
    "outputDir": "%LOCALAPPDATA%\\MeetNow\\Recordings",
    "sampleRate": 16000,
    "channels": 1,
    "bitDepth": 16
  },
  "vad": {
    "aggressiveness": 3,
    "frameSizeMs": 30,
    "hysteresisRequired": 3,
    "hysteresisWindow": 5
  },
  "chunking": {
    "preBufferSeconds": 5,
    "silenceTimeoutMs": 3000,
    "minChunkDurationMs": 2000,
    "maxChunkDurationMs": 300000,
    "maxChunkGraceMs": 30000,
    "micKeepaliveMs": 10000,
    "sessionGapMinutes": 10
  },
  "transcription": {
    "pythonPath": "python",
    "model": "small",
    "device": "cuda",
    "language": null,
    "pollIntervalSeconds": 2
  },
  "storage": {
    "archiveToFlac": true,
    "deleteWavAfterArchive": true,
    "minFreeDiskMb": 1000,
    "criticalFreeDiskMb": 500
  }
}
```

## Risks and Mitigations

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| WebRTC VAD false triggers | Medium | Low | Min duration filter + future Silero upgrade |
| Bluetooth device switch drops audio | Medium | Low | Device monitor flushes chunk on change |
| GPU VRAM contention | Low | Medium | Configurable model size, CPU fallback |
| Disk fills up | Low | High | Disk space monitoring with thresholds |
| Python process crashes | Medium | Low | Auto-restart, chunks queue on disk |
| Corporate policy prohibits recording | Medium | High | Disclaimer, visible indicator, user responsibility |
| Whisper hallucination on low-quality audio | Medium | Low | Word-level confidence scores, future filtering |

## Future Upgrades

### Near-Term
- Silero VAD via ONNX Runtime (replacing WebRTC)
- System tray icon with recording state indicator
- FLAC archival compression
- Configurable retention policy
- Integration into MeetNow (recording tied to meeting popups)

### Long-Term
- Local LLM for summaries, action items, decisions
- Speaker diarization beyond me/other
- Full-text search index over transcripts
- Live transcription display in MeetNow UI
- Per-app audio isolation (if Windows exposes future API)
