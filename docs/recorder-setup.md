# MeetNow Recorder — Setup Guide

Meeting recorder with automatic speech-to-text transcription. Records loopback (meeting audio) and microphone (your voice) separately, transcribes with Faster-Whisper, and produces human-readable transcripts with speaker labels and timestamps.

## Prerequisites

| Requirement | Version | Download |
|-------------|---------|----------|
| .NET SDK | 9.0+ | https://dotnet.microsoft.com/download |
| Python | 3.10+ | https://python.org/downloads |
| Windows | 10 (19041+) or 11 | — |

GPU is optional. The transcriber runs on CPU by default. For faster transcription, install CUDA 12 + cuBLAS and set `"device": "cuda"` in appsettings.json.

## Quick Setup

```powershell
git clone <repo-url>
cd MeetNow
powershell -ExecutionPolicy Bypass -File Setup-Recorder.ps1
```

The script:
1. Verifies .NET SDK and Python are installed
2. Installs Python packages (faster-whisper, resemblyzer, scikit-learn, soundfile)
3. Builds the recorder project
4. Copies the Python transcriber into the build output
5. Enables transcription in appsettings.json

## Running

After setup, start the recorder:

```powershell
dotnet run --project MeetNow.Recorder\MeetNow.Recorder.csproj --configuration Release --no-build
```

Or run the executable directly:

```
MeetNow.Recorder\bin\Release\net9.0-windows10.0.19041.0\win-x64\MeetNow.Recorder.exe
```

The app starts minimized to the system tray. No window appears — look for the icon in the taskbar notification area.

## Usage

### System Tray

- **Double-click** tray icon — opens the status dashboard
- **Right-click** tray icon — context menu:
  - Open Status Window
  - Pause / Resume Recording
  - Exit

### Tray Icon States

| Icon | Meaning |
|------|---------|
| Normal | Idle — no speech detected |
| Red dot | Recording — speech detected |
| Orange dot | Paused |

### Status Dashboard

Left pane shows all recording sessions. Right pane shows details for the selected session:

- **Active session**: live audio level meters, chunk placeholders filling in as transcription completes
- **Completed session**: transcript with speaker labels, audio player with seek, action buttons

### Session Status Icons

| Icon | Meaning |
|------|---------|
| Green dot | Fully transcribed — transcript.txt ready |
| Yellow dot | Completed, waiting for transcription |
| Orange dot | Transcription in progress |
| Red dot | Actively recording |
| Red X | Has failed transcription chunks |

### Audio Player

- **Channel toggle**: Loopback (meeting audio) / Mic (your voice) / Both
- **Click** green ▶ button next to any transcript line to play from that point
- Recording auto-pauses during playback to avoid feedback

### Action Buttons

- **Open Transcript** — opens transcript.txt in your default text editor
- **Open Folder** — opens the session directory in Explorer
- **Generate Transcript** — regenerates transcript.txt from chunk data
- **Retry Transcription** — resets failed chunks and re-transcribes
- **Complete Session** — marks a stale recording session as completed
- **Delete Session** — removes session and all recordings (with confirmation)

## Output

Recordings are stored in `%LOCALAPPDATA%\MeetNow\Recordings\`:

```
2026-03-30_09-16-33\
  session.json              — session metadata
  transcript.txt            — human-readable dialog transcript
  chunks\
    chunk_001.json          — chunk metadata + transcription status
    chunk_001_loopback.wav  — meeting audio
    chunk_001_mic.wav       — your microphone audio
  transcripts\
    chunk_001_loopback.json — Whisper output for meeting audio
    chunk_001_mic.json      — Whisper output for your voice
```

### Transcript Format

```
Meeting Transcript — 2026-03-30_09-16-33
Duration: 45:23
============================================================

[13:16:34] Other: We are planning so Boris will go through the document...
[13:16:46] Other: Okay, one more thing for Ramya...

[13:17:02] Me: Yes, I'll take care of that.

[13:17:05] Other: Great, thanks Boris.
```

## Configuration

Edit `appsettings.json` in the build output directory:

### Transcription

| Setting | Default | Description |
|---------|---------|-------------|
| `enabled` | `true` | Enable/disable automatic transcription |
| `model` | `small` | Whisper model: tiny, base, small, medium, large-v3 |
| `device` | `cpu` | `cpu` or `cuda` (requires CUDA 12 + cuBLAS) |
| `language` | `null` | Force language (e.g., `"en"`). Null = auto-detect |

### Recording

| Setting | Default | Description |
|---------|---------|-------------|
| `silenceTimeoutMs` | `3000` | Silence duration before chunk split |
| `maxChunkDurationMs` | `300000` | Maximum chunk length (5 minutes) |
| `sessionGapMinutes` | `10` | Gap before starting a new session |

### Transcription Speed

| Model | CPU Speed | GPU Speed | Quality |
|-------|-----------|-----------|---------|
| tiny | ~6x realtime | ~30x | Low |
| base | ~3x realtime | ~20x | Fair |
| small | ~1.5x realtime | ~10x | Good |
| medium | ~0.5x realtime | ~5x | Better |
| large-v3 | ~0.2x realtime | ~2x | Best |

"1.5x realtime" means 1 minute of audio takes ~40 seconds to transcribe.

## Logs

- Recorder log: `%TEMP%\MeetNow.Recorder<date>.log`
- Serilog rolling daily, 7-day retention

## Troubleshooting

**No tray icon visible**: Check the Windows notification area overflow (click ↑ arrow in taskbar). You may need to enable "always show" for MeetNow.Recorder in Settings → Personalization → Taskbar.

**Transcription produces gibberish**: The Whisper model may hallucinate on near-silent audio. Segments with mostly non-ASCII characters are automatically filtered. If you see junk in transcript.txt, click "Generate Transcript" to regenerate with the filter.

**"WebRtcVad.dll not found"**: The WebRtcVadSharp NuGet package needs to be a direct dependency. Run `Setup-Recorder.ps1` again to rebuild.

**Session stuck in "recording"**: Restart the recorder — it auto-completes stale sessions on startup.

**CUDA errors**: If you see "cublas64_12.dll not found", either install CUDA 12 toolkit or set `"device": "cpu"` in appsettings.json.
