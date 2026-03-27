# Meeting Recorder & Transcriber Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a fully local Windows 11 pipeline that captures dual-channel audio (loopback + mic), uses VAD to detect speech, writes time-aligned WAV chunks, and transcribes them via a Python Faster-Whisper sidecar process.

**Architecture:** File-drop pipeline. A C# console app captures audio and writes WAV chunks + JSON metadata to disk. A Python process polls for new chunks, transcribes with Faster-Whisper (GPU), and writes transcript JSON alongside them. The C# audio capture + VAD + chunking logic lives in a reusable class library (`MeetNow.Recording.Core`).

**Tech Stack:** .NET 8 (C#), NAudio 2.2.1, WebRtcVadSharp, xUnit, Python 3, Faster-Whisper, CUDA

**Spec:** `docs/superpowers/specs/2026-03-27-meeting-recorder-transcriber-design.md`

---

## File Structure

### New Projects

```
MeetNow.Recording.Contracts/              ← net8.0, zero dependencies
  MeetNow.Recording.Contracts.csproj
  ChunkStatus.cs
  SplitReason.cs
  VadStats.cs
  ChunkMetadata.cs
  SessionMetadata.cs
  TranscriptResult.cs
  TranscriptSegment.cs
  TranscriptWord.cs
  MergedTranscript.cs
  MergedSegment.cs
  SessionStatus.cs

MeetNow.Recording.Core/                   ← net8.0-windows10.0.19041.0, NAudio + WebRtcVadSharp
  MeetNow.Recording.Core.csproj
  Audio/
    AudioFormat.cs
    RingBuffer.cs
    LoopbackCaptureStream.cs
    MicrophoneCaptureStream.cs
    DualChannelCapture.cs
  Vad/
    IVoiceActivityDetector.cs
    WebRtcVoiceActivityDetector.cs
  Recording/
    RecorderState.cs
    RecordingStateMachine.cs
    ChunkTimeline.cs
    ChunkWriter.cs
    SessionManager.cs
  Config/
    RecorderConfig.cs
  DeviceMonitor.cs

MeetNow.Recording.Core.Tests/             ← xUnit test project
  MeetNow.Recording.Core.Tests.csproj
  Audio/
    RingBufferTests.cs
  Vad/
    RecordingStateMachineTests.cs
  Recording/
    ChunkTimelineTests.cs
    SessionManagerTests.cs
    ChunkWriterTests.cs

MeetNow.Recorder/                          ← net8.0-windows10.0.19041.0, console exe
  MeetNow.Recorder.csproj
  Program.cs
  RecorderService.cs
  TranscriberProcessManager.cs
  appsettings.json

MeetNow.Recorder.Transcriber/             ← Python package
  transcriber/
    __init__.py
    __main__.py
    config.py
    watcher.py
    transcribe.py
    merger.py
  requirements.txt
```

### Modified Files

```
MeetNow.sln                               ← add 4 new projects
```

---

### Task 1: Solution Scaffolding

**Files:**
- Create: `MeetNow.Recording.Contracts/MeetNow.Recording.Contracts.csproj`
- Create: `MeetNow.Recording.Core/MeetNow.Recording.Core.csproj`
- Create: `MeetNow.Recording.Core.Tests/MeetNow.Recording.Core.Tests.csproj`
- Create: `MeetNow.Recorder/MeetNow.Recorder.csproj`
- Modify: `MeetNow.sln`

- [ ] **Step 1: Create Contracts project**

```bash
cd C:/Users/Boris.Kudriashov/Source/repos/MeetNow
dotnet new classlib -n MeetNow.Recording.Contracts -f net8.0 -o MeetNow.Recording.Contracts
```

Then replace the generated csproj with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Delete the generated `Class1.cs`.

- [ ] **Step 2: Create Core library project**

```bash
dotnet new classlib -n MeetNow.Recording.Core -f net8.0-windows10.0.19041.0 -o MeetNow.Recording.Core
```

Replace the generated csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MeetNow.Recording.Contracts\MeetNow.Recording.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="NAudio" Version="2.2.1" />
    <PackageReference Include="WebRtcVadSharp" Version="1.3.0" />
  </ItemGroup>
</Project>
```

Delete the generated `Class1.cs`. Create subdirectories:

```bash
mkdir -p MeetNow.Recording.Core/Audio MeetNow.Recording.Core/Vad MeetNow.Recording.Core/Recording MeetNow.Recording.Core/Config
```

- [ ] **Step 3: Create test project**

```bash
dotnet new xunit -n MeetNow.Recording.Core.Tests -f net8.0-windows10.0.19041.0 -o MeetNow.Recording.Core.Tests
```

Replace the generated csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MeetNow.Recording.Core\MeetNow.Recording.Core.csproj" />
    <ProjectReference Include="..\MeetNow.Recording.Contracts\MeetNow.Recording.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.12.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
  </ItemGroup>
</Project>
```

Delete the generated `UnitTest1.cs`. Create subdirectories:

```bash
mkdir -p MeetNow.Recording.Core.Tests/Audio MeetNow.Recording.Core.Tests/Vad MeetNow.Recording.Core.Tests/Recording
```

- [ ] **Step 4: Create Recorder console project**

```bash
dotnet new console -n MeetNow.Recorder -f net8.0-windows10.0.19041.0 -o MeetNow.Recorder
```

Replace the generated csproj:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MeetNow.Recording.Core\MeetNow.Recording.Core.csproj" />
    <ProjectReference Include="..\MeetNow.Recording.Contracts\MeetNow.Recording.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.1" />
    <PackageReference Include="Serilog" Version="3.0.1" />
    <PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
  </ItemGroup>
</Project>
```

Delete the generated `Program.cs` (we'll write ours in a later task).

- [ ] **Step 5: Add all projects to the solution**

```bash
cd C:/Users/Boris.Kudriashov/Source/repos/MeetNow
dotnet sln add MeetNow.Recording.Contracts/MeetNow.Recording.Contracts.csproj
dotnet sln add MeetNow.Recording.Core/MeetNow.Recording.Core.csproj
dotnet sln add MeetNow.Recording.Core.Tests/MeetNow.Recording.Core.Tests.csproj
dotnet sln add MeetNow.Recorder/MeetNow.Recorder.csproj
```

- [ ] **Step 6: Verify the solution builds**

```bash
dotnet build MeetNow.sln
```

Expected: Build succeeded with 0 errors. Warnings about empty projects are fine.

- [ ] **Step 7: Commit**

```bash
git add MeetNow.Recording.Contracts/ MeetNow.Recording.Core/ MeetNow.Recording.Core.Tests/ MeetNow.Recorder/ MeetNow.sln
git commit -m "feat: scaffold recorder solution structure

Add four new projects:
- MeetNow.Recording.Contracts (shared schemas)
- MeetNow.Recording.Core (audio capture + VAD library)
- MeetNow.Recording.Core.Tests (xUnit)
- MeetNow.Recorder (console host)"
```

---

### Task 2: Contracts — All Shared Types

**Files:**
- Create: `MeetNow.Recording.Contracts/ChunkStatus.cs`
- Create: `MeetNow.Recording.Contracts/SplitReason.cs`
- Create: `MeetNow.Recording.Contracts/SessionStatus.cs`
- Create: `MeetNow.Recording.Contracts/VadStats.cs`
- Create: `MeetNow.Recording.Contracts/ChunkMetadata.cs`
- Create: `MeetNow.Recording.Contracts/SessionMetadata.cs`
- Create: `MeetNow.Recording.Contracts/TranscriptWord.cs`
- Create: `MeetNow.Recording.Contracts/TranscriptSegment.cs`
- Create: `MeetNow.Recording.Contracts/TranscriptResult.cs`
- Create: `MeetNow.Recording.Contracts/MergedSegment.cs`
- Create: `MeetNow.Recording.Contracts/MergedTranscript.cs`

- [ ] **Step 1: Create enums**

`MeetNow.Recording.Contracts/ChunkStatus.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ChunkStatus>))]
public enum ChunkStatus
{
    [JsonStringEnumMemberName("pending_transcription")]
    PendingTranscription,
    [JsonStringEnumMemberName("transcribing")]
    Transcribing,
    [JsonStringEnumMemberName("transcribed")]
    Transcribed,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("archived")]
    Archived
}
```

`MeetNow.Recording.Contracts/SplitReason.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SplitReason>))]
public enum SplitReason
{
    [JsonStringEnumMemberName("silence_timeout")]
    SilenceTimeout,
    [JsonStringEnumMemberName("max_duration")]
    MaxDuration,
    [JsonStringEnumMemberName("device_change")]
    DeviceChange,
    [JsonStringEnumMemberName("manual_stop")]
    ManualStop
}
```

`MeetNow.Recording.Contracts/SessionStatus.cs`:

```csharp
using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus
{
    [JsonStringEnumMemberName("recording")]
    Recording,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("transcribed")]
    Transcribed
}
```

- [ ] **Step 2: Create VadStats and metadata types**

`MeetNow.Recording.Contracts/VadStats.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class VadStats
{
    public int SpeechFrames { get; set; }
    public int TotalFrames { get; set; }
    public double SpeechRatio => TotalFrames > 0 ? (double)SpeechFrames / TotalFrames : 0;
}
```

`MeetNow.Recording.Contracts/ChunkMetadata.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class ChunkMetadata
{
    public int ChunkIndex { get; set; }
    public string SessionId { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public DateTime EndTimeUtc { get; set; }
    public double DurationSeconds { get; set; }
    public string LoopbackFile { get; set; } = "";
    public string MicFile { get; set; } = "";
    public VadStats VadStats { get; set; } = new();
    public SplitReason SplitReason { get; set; }
    public ChunkStatus Status { get; set; }
    public DateTime? ClaimedAtUtc { get; set; }
    public string? Error { get; set; }
}
```

`MeetNow.Recording.Contracts/SessionMetadata.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class CaptureDeviceInfo
{
    public string Loopback { get; set; } = "";
    public string Microphone { get; set; } = "";
}

public class SessionConfig
{
    public int VadMode { get; set; }
    public int SilenceTimeoutMs { get; set; }
    public int MaxChunkDurationMs { get; set; }
}

public class SessionMetadata
{
    public string SessionId { get; set; } = "";
    public DateTime StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }
    public int ChunkCount { get; set; }
    public SessionStatus Status { get; set; }
    public CaptureDeviceInfo CaptureDevices { get; set; } = new();
    public SessionConfig Config { get; set; } = new();
}
```

- [ ] **Step 3: Create transcript types**

`MeetNow.Recording.Contracts/TranscriptWord.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class TranscriptWord
{
    public string Word { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public double Probability { get; set; }
}
```

`MeetNow.Recording.Contracts/TranscriptSegment.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class TranscriptSegment
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Text { get; set; } = "";
    public List<TranscriptWord> Words { get; set; } = [];
}
```

`MeetNow.Recording.Contracts/TranscriptResult.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class TranscriptResult
{
    public int ChunkIndex { get; set; }
    public string Channel { get; set; } = "";
    public string Language { get; set; } = "";
    public double LanguageProbability { get; set; }
    public List<TranscriptSegment> Segments { get; set; } = [];
    public double TranscriptionTimeSeconds { get; set; }
    public string ModelName { get; set; } = "";
    public DateTime TimestampUtcBase { get; set; }
}
```

`MeetNow.Recording.Contracts/MergedSegment.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class MergedSegment
{
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public string Speaker { get; set; } = "";
    public string Text { get; set; } = "";
}
```

`MeetNow.Recording.Contracts/MergedTranscript.cs`:

```csharp
namespace MeetNow.Recording.Contracts;

public class ChannelTranscripts
{
    public List<MergedSegment> Loopback { get; set; } = [];
    public List<MergedSegment> Mic { get; set; } = [];
}

public class MergedTranscript
{
    public string SessionId { get; set; } = "";
    public string Duration { get; set; } = "";
    public ChannelTranscripts Channels { get; set; } = new();
    public List<MergedSegment> Merged { get; set; } = [];
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build MeetNow.Recording.Contracts/MeetNow.Recording.Contracts.csproj
```

Expected: Build succeeded, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Contracts/
git commit -m "feat: add Recording.Contracts with all shared types

Enums: ChunkStatus, SplitReason, SessionStatus
Models: ChunkMetadata, SessionMetadata, VadStats,
        TranscriptResult, TranscriptSegment, TranscriptWord,
        MergedTranscript, MergedSegment"
```

---

### Task 3: RingBuffer with Tests (TDD)

**Files:**
- Create: `MeetNow.Recording.Core/Audio/RingBuffer.cs`
- Create: `MeetNow.Recording.Core.Tests/Audio/RingBufferTests.cs`

- [ ] **Step 1: Write failing tests**

`MeetNow.Recording.Core.Tests/Audio/RingBufferTests.cs`:

```csharp
using MeetNow.Recording.Core.Audio;

namespace MeetNow.Recording.Core.Tests.Audio;

public class RingBufferTests
{
    [Fact]
    public void Write_ThenDrain_ReturnsWrittenSamples()
    {
        var buffer = new RingBuffer(capacity: 10);
        short[] data = [1, 2, 3, 4, 5];

        buffer.Write(data);
        var result = buffer.Drain();

        Assert.Equal(data, result);
    }

    [Fact]
    public void Write_OverCapacity_OverwritesOldestSamples()
    {
        var buffer = new RingBuffer(capacity: 4);

        buffer.Write([1, 2, 3]);
        buffer.Write([4, 5, 6]);

        var result = buffer.Drain();
        Assert.Equal([3, 4, 5, 6], result);
    }

    [Fact]
    public void Drain_EmptyBuffer_ReturnsEmptyArray()
    {
        var buffer = new RingBuffer(capacity: 10);
        var result = buffer.Drain();
        Assert.Empty(result);
    }

    [Fact]
    public void Drain_ClearsBuffer()
    {
        var buffer = new RingBuffer(capacity: 10);
        buffer.Write([1, 2, 3]);
        buffer.Drain();

        var result = buffer.Drain();
        Assert.Empty(result);
    }

    [Fact]
    public void Write_ExactCapacity_ReturnsAll()
    {
        var buffer = new RingBuffer(capacity: 5);
        buffer.Write([1, 2, 3, 4, 5]);

        var result = buffer.Drain();
        Assert.Equal([1, 2, 3, 4, 5], result);
    }

    [Fact]
    public void Write_WrapAround_MaintainsOrder()
    {
        var buffer = new RingBuffer(capacity: 5);

        buffer.Write([1, 2, 3, 4, 5]);
        buffer.Write([6, 7]);

        var result = buffer.Drain();
        Assert.Equal([3, 4, 5, 6, 7], result);
    }

    [Fact]
    public void Count_ReflectsWrittenSamples()
    {
        var buffer = new RingBuffer(capacity: 10);

        Assert.Equal(0, buffer.Count);
        buffer.Write([1, 2, 3]);
        Assert.Equal(3, buffer.Count);
    }

    [Fact]
    public void Count_CapsAtCapacity()
    {
        var buffer = new RingBuffer(capacity: 4);
        buffer.Write([1, 2, 3, 4, 5, 6]);
        Assert.Equal(4, buffer.Count);
    }

    [Fact]
    public void Write_LargerThanCapacity_KeepsLastCapacitySamples()
    {
        var buffer = new RingBuffer(capacity: 3);
        buffer.Write([1, 2, 3, 4, 5, 6, 7]);

        var result = buffer.Drain();
        Assert.Equal([5, 6, 7], result);
    }

    [Fact]
    public void MultipleSmallWrites_ThenDrain()
    {
        var buffer = new RingBuffer(capacity: 6);
        buffer.Write([1, 2]);
        buffer.Write([3, 4]);
        buffer.Write([5, 6]);

        var result = buffer.Drain();
        Assert.Equal([1, 2, 3, 4, 5, 6], result);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~RingBufferTests" --no-restore
```

Expected: Build failure — `RingBuffer` type does not exist.

- [ ] **Step 3: Implement RingBuffer**

`MeetNow.Recording.Core/Audio/RingBuffer.cs`:

```csharp
namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Thread-safe circular buffer of PCM samples (16-bit signed).
/// Used as a rolling pre-buffer so speech onset is never clipped.
/// </summary>
public class RingBuffer
{
    private readonly short[] _buffer;
    private readonly int _capacity;
    private int _writePos;
    private int _count;
    private readonly object _lock = new();

    public RingBuffer(int capacity)
    {
        _capacity = capacity;
        _buffer = new short[capacity];
    }

    public int Count
    {
        get { lock (_lock) return _count; }
    }

    public void Write(ReadOnlySpan<short> data)
    {
        lock (_lock)
        {
            // If data is larger than capacity, skip to the last _capacity samples
            var source = data;
            if (source.Length > _capacity)
            {
                source = source[^_capacity..];
                _writePos = 0;
                _count = 0;
            }

            foreach (var sample in source)
            {
                _buffer[_writePos] = sample;
                _writePos = (_writePos + 1) % _capacity;
                if (_count < _capacity)
                    _count++;
            }
        }
    }

    /// <summary>
    /// Returns all buffered samples in chronological order and resets the buffer.
    /// </summary>
    public short[] Drain()
    {
        lock (_lock)
        {
            if (_count == 0)
                return [];

            var result = new short[_count];
            var readPos = (_writePos - _count + _capacity) % _capacity;

            for (int i = 0; i < _count; i++)
            {
                result[i] = _buffer[(readPos + i) % _capacity];
            }

            _count = 0;
            _writePos = 0;

            return result;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~RingBufferTests" -v normal
```

Expected: All 10 tests pass.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Core/Audio/RingBuffer.cs MeetNow.Recording.Core.Tests/Audio/RingBufferTests.cs
git commit -m "feat: add RingBuffer with TDD tests

Thread-safe circular buffer for PCM pre-buffering.
Handles wrap-around, over-capacity writes, and drain."
```

---

### Task 4: AudioFormat Constants and RecorderConfig

**Files:**
- Create: `MeetNow.Recording.Core/Audio/AudioFormat.cs`
- Create: `MeetNow.Recording.Core/Config/RecorderConfig.cs`

- [ ] **Step 1: Create AudioFormat constants**

`MeetNow.Recording.Core/Audio/AudioFormat.cs`:

```csharp
using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

public static class AudioFormat
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int BitsPerSample = 16;
    public const int BytesPerSample = BitsPerSample / 8;
    public const int SamplesPerSecond = SampleRate * Channels;
    public const int BytesPerSecond = SamplesPerSecond * BytesPerSample;

    /// <summary>16kHz mono 16-bit PCM — ASR-friendly normalized format.</summary>
    public static readonly WaveFormat WaveFormat = new(SampleRate, BitsPerSample, Channels);

    /// <summary>Number of samples in one VAD frame (30ms at 16kHz).</summary>
    public const int VadFrameSamples = SampleRate * 30 / 1000; // 480

    /// <summary>Pre-buffer capacity in samples for a given duration in seconds.</summary>
    public static int PreBufferSamples(int seconds) => SampleRate * seconds;
}
```

- [ ] **Step 2: Create RecorderConfig**

`MeetNow.Recording.Core/Config/RecorderConfig.cs`:

```csharp
namespace MeetNow.Recording.Core.Config;

public class RecorderConfig
{
    // Recording
    public string OutputDir { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetNow", "Recordings");

    // VAD
    public int VadAggressiveness { get; set; } = 3;
    public int VadFrameSizeMs { get; set; } = 30;
    public int HysteresisRequired { get; set; } = 3;
    public int HysteresisWindow { get; set; } = 5;

    // Chunking
    public int PreBufferSeconds { get; set; } = 5;
    public int SilenceTimeoutMs { get; set; } = 3000;
    public int MinChunkDurationMs { get; set; } = 2000;
    public int MaxChunkDurationMs { get; set; } = 300_000;
    public int MaxChunkGraceMs { get; set; } = 30_000;
    public int MicKeepaliveMs { get; set; } = 10_000;
    public int SessionGapMinutes { get; set; } = 10;

    // Transcription
    public string PythonPath { get; set; } = "python";
    public string TranscriberModel { get; set; } = "small";
    public string TranscriberDevice { get; set; } = "cuda";
    public string? TranscriberLanguage { get; set; }
    public int TranscriberPollIntervalSeconds { get; set; } = 2;

    // Storage
    public bool ArchiveToFlac { get; set; } = true;
    public bool DeleteWavAfterArchive { get; set; } = true;
    public int MinFreeDiskMb { get; set; } = 1000;
    public int CriticalFreeDiskMb { get; set; } = 500;

    // Derived
    public int SilenceTimeoutFrames => SilenceTimeoutMs / VadFrameSizeMs;
    public int MinChunkDurationFrames => MinChunkDurationMs / VadFrameSizeMs;
    public int MaxChunkDurationFrames => MaxChunkDurationMs / VadFrameSizeMs;
    public int MaxChunkGraceFrames => MaxChunkGraceMs / VadFrameSizeMs;
    public int MicKeepaliveFrames => MicKeepaliveMs / VadFrameSizeMs;
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build MeetNow.Recording.Core/MeetNow.Recording.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recording.Core/Audio/AudioFormat.cs MeetNow.Recording.Core/Config/RecorderConfig.cs
git commit -m "feat: add AudioFormat constants and RecorderConfig

16kHz mono 16-bit PCM format constants.
Full configuration with all spec defaults."
```

---

### Task 5: VAD Interface and WebRTC Implementation

**Files:**
- Create: `MeetNow.Recording.Core/Vad/IVoiceActivityDetector.cs`
- Create: `MeetNow.Recording.Core/Vad/WebRtcVoiceActivityDetector.cs`

- [ ] **Step 1: Create VAD interface**

`MeetNow.Recording.Core/Vad/IVoiceActivityDetector.cs`:

```csharp
namespace MeetNow.Recording.Core.Vad;

/// <summary>
/// Classifies audio frames as speech or non-speech.
/// Implementations must accept 30ms frames of 16kHz mono 16-bit PCM (480 samples).
/// </summary>
public interface IVoiceActivityDetector : IDisposable
{
    bool IsSpeech(ReadOnlySpan<short> frame);
}
```

- [ ] **Step 2: Create WebRTC VAD implementation**

`MeetNow.Recording.Core/Vad/WebRtcVoiceActivityDetector.cs`:

```csharp
using WebRtcVadSharp;

namespace MeetNow.Recording.Core.Vad;

public class WebRtcVoiceActivityDetector : IVoiceActivityDetector
{
    private readonly WebRtcVad _vad;

    public WebRtcVoiceActivityDetector(int aggressiveness = 3)
    {
        _vad = new WebRtcVad
        {
            OperatingMode = aggressiveness switch
            {
                0 => OperatingMode.Quality,
                1 => OperatingMode.LowBitrate,
                2 => OperatingMode.Aggressive,
                3 => OperatingMode.VeryAggressive,
                _ => OperatingMode.VeryAggressive
            },
            FrameLength = FrameLength.Is30ms,
            SampleRate = SampleRate.Is16kHz
        };
    }

    public bool IsSpeech(ReadOnlySpan<short> frame)
    {
        // WebRtcVadSharp expects a byte array (little-endian 16-bit PCM)
        var bytes = new byte[frame.Length * 2];
        for (int i = 0; i < frame.Length; i++)
        {
            bytes[i * 2] = (byte)(frame[i] & 0xFF);
            bytes[i * 2 + 1] = (byte)((frame[i] >> 8) & 0xFF);
        }

        return _vad.HasSpeech(bytes);
    }

    public void Dispose()
    {
        _vad.Dispose();
    }
}
```

- [ ] **Step 3: Verify build**

```bash
dotnet build MeetNow.Recording.Core/MeetNow.Recording.Core.csproj
```

Expected: Build succeeded. If `WebRtcVadSharp` API differs from expected, adjust accordingly — check the NuGet package's actual API surface and adapt the wrapper.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recording.Core/Vad/
git commit -m "feat: add IVoiceActivityDetector and WebRTC implementation

Interface-based VAD for future Silero replacement.
WebRTC VAD wrapper with configurable aggressiveness (0-3)."
```

---

### Task 6: RecordingStateMachine with Tests (TDD)

**Files:**
- Create: `MeetNow.Recording.Core/Recording/RecorderState.cs`
- Create: `MeetNow.Recording.Core/Recording/RecordingStateMachine.cs`
- Create: `MeetNow.Recording.Core.Tests/Vad/RecordingStateMachineTests.cs`

This is the most complex unit. The state machine has 5 states: IDLE, RECORDING, MIC_KEEPALIVE, DRAINING, FLUSHING.

- [ ] **Step 1: Create the state enum**

`MeetNow.Recording.Core/Recording/RecorderState.cs`:

```csharp
namespace MeetNow.Recording.Core.Recording;

public enum RecorderState
{
    Idle,
    Recording,
    MicKeepalive,
    Draining,
    Flushing
}
```

- [ ] **Step 2: Write failing tests for the state machine**

`MeetNow.Recording.Core.Tests/Vad/RecordingStateMachineTests.cs`:

```csharp
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recording.Core.Tests.Vad;

public class RecordingStateMachineTests
{
    private readonly RecorderConfig _config = new()
    {
        HysteresisRequired = 3,
        HysteresisWindow = 5,
        SilenceTimeoutMs = 3000,
        VadFrameSizeMs = 30,
        MinChunkDurationMs = 2000,
        MaxChunkDurationMs = 300_000,
        MaxChunkGraceMs = 30_000,
        MicKeepaliveMs = 10_000
    };

    private RecordingStateMachine CreateMachine() => new(_config);

    [Fact]
    public void InitialState_IsIdle()
    {
        var sm = CreateMachine();
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void SingleSpeechFrame_DoesNotTransitionToRecording()
    {
        var sm = CreateMachine();
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void HysteresisMetOnLoopback_TransitionsToRecording()
    {
        var sm = CreateMachine();
        // 3 speech frames within 5-frame window
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void HysteresisWithGaps_StillTransitions()
    {
        var sm = CreateMachine();
        // 3 of 5 frames are speech
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void HysteresisNotMet_StaysIdle()
    {
        var sm = CreateMachine();
        // Only 2 of 5 frames are speech
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void MicSpeechAlone_DoesNotOpenChunk()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 10; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Idle, sm.State);
    }

    [Fact]
    public void Recording_LoopbackSilence_TransitionsToMicKeepalive_WhenMicActive()
    {
        var sm = CreateMachine();
        // Enter recording
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        // Loopback goes silent, mic is active
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);
    }

    [Fact]
    public void Recording_LoopbackSilence_TransitionsToDraining_WhenMicSilent()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void MicKeepalive_LoopbackResumes_BackToRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void MicKeepalive_MicGoeSilent_AdvancesToDraining()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.MicKeepalive, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void MicKeepalive_Expires_AdvancesToDraining()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        // 10_000ms / 30ms = 333 frames for mic keepalive
        int keepaliveFrames = _config.MicKeepaliveFrames;
        for (int i = 0; i < keepaliveFrames; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);

        // Next frame should be in Draining (keepalive expired)
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void Draining_LoopbackResumes_BackToRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);
    }

    [Fact]
    public void Draining_MicSpeech_DoesNotResumeChunk()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        Assert.Equal(RecorderState.Draining, sm.State);

        sm.ProcessFrame(loopbackSpeech: false, micSpeech: true);
        Assert.Equal(RecorderState.Draining, sm.State);
    }

    [Fact]
    public void Draining_SilenceTimeout_TransitionsToFlushing()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        // 3000ms / 30ms = 100 silence frames
        int silenceFrames = _config.SilenceTimeoutFrames;
        SplitReason? reason = null;
        sm.OnFlush += r => reason = r;

        for (int i = 0; i < silenceFrames + 1; i++)
            sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.Equal(SplitReason.SilenceTimeout, reason);
    }

    [Fact]
    public void MaxChunkDuration_TriggersFlush()
    {
        var config = new RecorderConfig
        {
            HysteresisRequired = 1,
            HysteresisWindow = 1,
            MaxChunkDurationMs = 90, // 3 frames at 30ms
            MaxChunkGraceMs = 60,    // 2 frames grace
            VadFrameSizeMs = 30,
            SilenceTimeoutMs = 3000,
            MinChunkDurationMs = 0,
            MicKeepaliveMs = 10_000
        };
        var sm = new RecordingStateMachine(config);
        SplitReason? reason = null;
        sm.OnFlush += r => reason = r;

        // Enter recording
        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        // Continuous speech for max + grace (3 + 2 = 5 frames)
        for (int i = 0; i < 5; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.Equal(SplitReason.MaxDuration, reason);
    }

    [Fact]
    public void MinChunkDuration_DiscardsTooShortChunks()
    {
        var config = new RecorderConfig
        {
            HysteresisRequired = 1,
            HysteresisWindow = 1,
            MinChunkDurationMs = 2000, // 66 frames
            SilenceTimeoutMs = 30,     // 1 frame
            VadFrameSizeMs = 30,
            MaxChunkDurationMs = 300_000,
            MaxChunkGraceMs = 30_000,
            MicKeepaliveMs = 10_000
        };
        var sm = new RecordingStateMachine(config);
        bool flushed = false;
        bool discarded = false;
        sm.OnFlush += _ => flushed = true;
        sm.OnDiscard += () => discarded = true;

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(RecorderState.Recording, sm.State);

        // Immediate silence — chunk is too short
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);
        sm.ProcessFrame(loopbackSpeech: false, micSpeech: false);

        Assert.Equal(RecorderState.Idle, sm.State);
        Assert.False(flushed);
        Assert.True(discarded);
    }

    [Fact]
    public void FrameCount_IncrementsWhileRecording()
    {
        var sm = CreateMachine();
        for (int i = 0; i < 3; i++)
            sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);

        Assert.Equal(3, sm.RecordingFrameCount);

        sm.ProcessFrame(loopbackSpeech: true, micSpeech: false);
        Assert.Equal(4, sm.RecordingFrameCount);
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~RecordingStateMachineTests" --no-restore
```

Expected: Build failure — `RecordingStateMachine` does not exist.

- [ ] **Step 4: Implement RecordingStateMachine**

`MeetNow.Recording.Core/Recording/RecordingStateMachine.cs`:

```csharp
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Recording;

public class RecordingStateMachine
{
    private readonly RecorderConfig _config;
    private readonly Queue<bool> _hysteresisWindow = new();

    public RecorderState State { get; private set; } = RecorderState.Idle;
    public int RecordingFrameCount { get; private set; }
    public int SpeechFrameCount { get; private set; }

    private int _silenceFrameCount;
    private int _micKeepaliveFrameCount;
    private bool _maxDurationReached;

    public event Action<SplitReason>? OnFlush;
    public event Action? OnDiscard;
    public event Action? OnRecordingStarted;

    public RecordingStateMachine(RecorderConfig config)
    {
        _config = config;
    }

    public void ProcessFrame(bool loopbackSpeech, bool micSpeech)
    {
        switch (State)
        {
            case RecorderState.Idle:
                ProcessIdle(loopbackSpeech);
                break;
            case RecorderState.Recording:
                ProcessRecording(loopbackSpeech, micSpeech);
                break;
            case RecorderState.MicKeepalive:
                ProcessMicKeepalive(loopbackSpeech, micSpeech);
                break;
            case RecorderState.Draining:
                ProcessDraining(loopbackSpeech);
                break;
        }
    }

    public void ForceFlush(SplitReason reason)
    {
        if (State == RecorderState.Idle)
            return;

        if (RecordingFrameCount >= _config.MinChunkDurationFrames)
        {
            Flush(reason);
        }
        else
        {
            Discard();
        }
    }

    private void ProcessIdle(bool loopbackSpeech)
    {
        _hysteresisWindow.Enqueue(loopbackSpeech);
        if (_hysteresisWindow.Count > _config.HysteresisWindow)
            _hysteresisWindow.Dequeue();

        int speechCount = _hysteresisWindow.Count(x => x);
        if (speechCount >= _config.HysteresisRequired)
        {
            State = RecorderState.Recording;
            RecordingFrameCount = _hysteresisWindow.Count;
            SpeechFrameCount = speechCount;
            _silenceFrameCount = 0;
            _micKeepaliveFrameCount = 0;
            _maxDurationReached = false;
            _hysteresisWindow.Clear();
            OnRecordingStarted?.Invoke();
        }
    }

    private void ProcessRecording(bool loopbackSpeech, bool micSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (CheckMaxDuration())
            return;

        if (!loopbackSpeech)
        {
            if (micSpeech)
            {
                State = RecorderState.MicKeepalive;
                _micKeepaliveFrameCount = 1;
            }
            else
            {
                State = RecorderState.Draining;
                _silenceFrameCount = 1;
            }
        }
    }

    private void ProcessMicKeepalive(bool loopbackSpeech, bool micSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (CheckMaxDuration())
            return;

        if (loopbackSpeech)
        {
            State = RecorderState.Recording;
            _micKeepaliveFrameCount = 0;
            return;
        }

        _micKeepaliveFrameCount++;

        if (!micSpeech)
        {
            State = RecorderState.Draining;
            _silenceFrameCount = 1;
            _micKeepaliveFrameCount = 0;
            return;
        }

        if (_micKeepaliveFrameCount > _config.MicKeepaliveFrames)
        {
            State = RecorderState.Draining;
            _silenceFrameCount = 1;
            _micKeepaliveFrameCount = 0;
        }
    }

    private void ProcessDraining(bool loopbackSpeech)
    {
        RecordingFrameCount++;
        if (loopbackSpeech) SpeechFrameCount++;

        if (loopbackSpeech)
        {
            State = RecorderState.Recording;
            _silenceFrameCount = 0;
            return;
        }

        _silenceFrameCount++;

        if (_silenceFrameCount > _config.SilenceTimeoutFrames)
        {
            if (RecordingFrameCount >= _config.MinChunkDurationFrames)
            {
                Flush(SplitReason.SilenceTimeout);
            }
            else
            {
                Discard();
            }
        }
    }

    private bool CheckMaxDuration()
    {
        if (RecordingFrameCount < _config.MaxChunkDurationFrames)
        {
            _maxDurationReached = false;
            return false;
        }

        if (!_maxDurationReached)
            _maxDurationReached = true;

        // In grace window — look for a silence gap
        int graceFrames = RecordingFrameCount - _config.MaxChunkDurationFrames;
        if (graceFrames >= _config.MaxChunkGraceFrames)
        {
            Flush(SplitReason.MaxDuration);
            return true;
        }

        return false;
    }

    private void Flush(SplitReason reason)
    {
        State = RecorderState.Idle;
        var frameCount = RecordingFrameCount;
        var speechCount = SpeechFrameCount;
        RecordingFrameCount = 0;
        SpeechFrameCount = 0;
        _silenceFrameCount = 0;
        _micKeepaliveFrameCount = 0;
        _maxDurationReached = false;
        OnFlush?.Invoke(reason);
    }

    private void Discard()
    {
        State = RecorderState.Idle;
        RecordingFrameCount = 0;
        SpeechFrameCount = 0;
        _silenceFrameCount = 0;
        _micKeepaliveFrameCount = 0;
        _maxDurationReached = false;
        OnDiscard?.Invoke();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~RecordingStateMachineTests" -v normal
```

Expected: All 14 tests pass.

- [ ] **Step 6: Commit**

```bash
git add MeetNow.Recording.Core/Recording/RecorderState.cs MeetNow.Recording.Core/Recording/RecordingStateMachine.cs MeetNow.Recording.Core.Tests/Vad/RecordingStateMachineTests.cs
git commit -m "feat: add RecordingStateMachine with 5-state VAD logic

States: Idle → Recording → MicKeepalive → Draining → Flushing → Idle
Hysteresis, mic keepalive, max duration with grace, min chunk discard.
14 TDD tests covering all transitions and edge cases."
```

---

### Task 7: ChunkTimeline with Tests (TDD)

**Files:**
- Create: `MeetNow.Recording.Core/Recording/ChunkTimeline.cs`
- Create: `MeetNow.Recording.Core.Tests/Recording/ChunkTimelineTests.cs`

- [ ] **Step 1: Write failing tests**

`MeetNow.Recording.Core.Tests/Recording/ChunkTimelineTests.cs`:

```csharp
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recording.Core.Tests.Recording;

public class ChunkTimelineTests
{
    [Fact]
    public void Start_SetsStartTime()
    {
        var timeline = new ChunkTimeline();
        var before = DateTime.UtcNow;
        timeline.Start(chunkIndex: 1);
        var after = DateTime.UtcNow;

        Assert.True(timeline.StartTimeUtc >= before);
        Assert.True(timeline.StartTimeUtc <= after);
        Assert.Equal(1, timeline.ChunkIndex);
        Assert.True(timeline.IsActive);
    }

    [Fact]
    public void Stop_SetsEndTimeAndDuration()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);

        // Simulate passage of time with frame count
        timeline.AddFrames(100);
        timeline.Stop();

        Assert.False(timeline.IsActive);
        Assert.True(timeline.EndTimeUtc >= timeline.StartTimeUtc);
        Assert.True(timeline.DurationSeconds > 0);
    }

    [Fact]
    public void AddFrames_IncrementsTotalFrames()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddFrames(10);
        timeline.AddFrames(20);

        Assert.Equal(30, timeline.TotalFrames);
    }

    [Fact]
    public void AddSpeechFrame_IncrementsSpeechFrames()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddSpeechFrame();
        timeline.AddSpeechFrame();

        Assert.Equal(2, timeline.SpeechFrames);
    }

    [Fact]
    public void Reset_ClearsAllState()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        timeline.AddFrames(50);
        timeline.AddSpeechFrame();
        timeline.Stop();
        timeline.Reset();

        Assert.False(timeline.IsActive);
        Assert.Equal(0, timeline.TotalFrames);
        Assert.Equal(0, timeline.SpeechFrames);
        Assert.Equal(0, timeline.ChunkIndex);
    }

    [Fact]
    public void DurationSeconds_BasedOnFrameCountAndSampleRate()
    {
        var timeline = new ChunkTimeline();
        timeline.Start(chunkIndex: 1);
        // 480 samples per frame at 16kHz = 30ms per frame
        // 100 frames = 3 seconds
        timeline.AddFrames(100);
        timeline.Stop();

        Assert.InRange(timeline.DurationSeconds, 2.9, 3.1);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~ChunkTimelineTests" --no-restore
```

Expected: Build failure — `ChunkTimeline` does not exist.

- [ ] **Step 3: Implement ChunkTimeline**

`MeetNow.Recording.Core/Recording/ChunkTimeline.cs`:

```csharp
using MeetNow.Recording.Core.Audio;

namespace MeetNow.Recording.Core.Recording;

/// <summary>
/// Synchronized clock for a chunk across both loopback and mic channels.
/// Tracks start/end timestamps, frame counts, and speech statistics.
/// </summary>
public class ChunkTimeline
{
    public int ChunkIndex { get; private set; }
    public DateTime StartTimeUtc { get; private set; }
    public DateTime EndTimeUtc { get; private set; }
    public bool IsActive { get; private set; }
    public int TotalFrames { get; private set; }
    public int SpeechFrames { get; private set; }

    public double DurationSeconds => TotalFrames * AudioFormat.VadFrameSamples / (double)AudioFormat.SampleRate;

    public void Start(int chunkIndex)
    {
        ChunkIndex = chunkIndex;
        StartTimeUtc = DateTime.UtcNow;
        IsActive = true;
        TotalFrames = 0;
        SpeechFrames = 0;
    }

    public void Stop()
    {
        EndTimeUtc = DateTime.UtcNow;
        IsActive = false;
    }

    public void AddFrames(int count)
    {
        TotalFrames += count;
    }

    public void AddSpeechFrame()
    {
        SpeechFrames++;
    }

    public void Reset()
    {
        ChunkIndex = 0;
        StartTimeUtc = default;
        EndTimeUtc = default;
        IsActive = false;
        TotalFrames = 0;
        SpeechFrames = 0;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~ChunkTimelineTests" -v normal
```

Expected: All 6 tests pass.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Core/Recording/ChunkTimeline.cs MeetNow.Recording.Core.Tests/Recording/ChunkTimelineTests.cs
git commit -m "feat: add ChunkTimeline for synchronized dual-channel timing

Tracks start/end UTC, frame counts, speech stats.
Duration computed from frame count × sample rate."
```

---

### Task 8: SessionManager with Tests (TDD)

**Files:**
- Create: `MeetNow.Recording.Core/Recording/SessionManager.cs`
- Create: `MeetNow.Recording.Core.Tests/Recording/SessionManagerTests.cs`

- [ ] **Step 1: Write failing tests**

`MeetNow.Recording.Core.Tests/Recording/SessionManagerTests.cs`:

```csharp
using System.Text.Json;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recording.Core.Tests.Recording;

public class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly RecorderConfig _config;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "MeetNowTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _config = new RecorderConfig { OutputDir = _tempDir, SessionGapMinutes = 10 };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void StartNewSession_CreatesDirectoryAndSessionJson()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("TestLoopback", "TestMic");

        Assert.True(Directory.Exists(session.SessionDir));
        Assert.True(Directory.Exists(Path.Combine(session.SessionDir, "chunks")));
        Assert.True(Directory.Exists(Path.Combine(session.SessionDir, "transcripts")));
        Assert.True(File.Exists(Path.Combine(session.SessionDir, "session.json")));
    }

    [Fact]
    public void StartNewSession_WritesCorrectMetadata()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("Speakers (Test)", "Mic (Test)");

        var json = File.ReadAllText(Path.Combine(session.SessionDir, "session.json"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(SessionStatus.Recording, meta.Status);
        Assert.Equal("Speakers (Test)", meta.CaptureDevices.Loopback);
        Assert.Equal("Mic (Test)", meta.CaptureDevices.Microphone);
        Assert.Equal(0, meta.ChunkCount);
        Assert.Null(meta.EndTimeUtc);
    }

    [Fact]
    public void SessionId_MatchesFolderName()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");
        var folderName = Path.GetFileName(session.SessionDir);
        Assert.Equal(folderName, session.SessionId);
    }

    [Fact]
    public void NextChunkIndex_Increments()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");

        Assert.Equal(1, session.NextChunkIndex());
        Assert.Equal(2, session.NextChunkIndex());
        Assert.Equal(3, session.NextChunkIndex());
    }

    [Fact]
    public void CompleteSession_UpdatesMetadata()
    {
        var manager = new SessionManager(_config);
        var session = manager.StartNewSession("lb", "mic");
        session.NextChunkIndex();
        session.NextChunkIndex();
        session.Complete();

        var json = File.ReadAllText(Path.Combine(session.SessionDir, "session.json"));
        var meta = JsonSerializer.Deserialize<SessionMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(SessionStatus.Completed, meta.Status);
        Assert.NotNull(meta.EndTimeUtc);
        Assert.Equal(2, meta.ChunkCount);
    }

    [Fact]
    public void ShouldStartNewSession_TrueWhenNoActiveSession()
    {
        var manager = new SessionManager(_config);
        Assert.True(manager.ShouldStartNewSession(lastChunkTimeUtc: null));
    }

    [Fact]
    public void ShouldStartNewSession_TrueWhenGapExceedsThreshold()
    {
        var manager = new SessionManager(_config);
        var lastChunkTime = DateTime.UtcNow.AddMinutes(-11);
        Assert.True(manager.ShouldStartNewSession(lastChunkTime));
    }

    [Fact]
    public void ShouldStartNewSession_FalseWhenWithinGap()
    {
        var manager = new SessionManager(_config);
        var lastChunkTime = DateTime.UtcNow.AddMinutes(-5);
        Assert.False(manager.ShouldStartNewSession(lastChunkTime));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~SessionManagerTests" --no-restore
```

Expected: Build failure — `SessionManager` does not exist.

- [ ] **Step 3: Implement SessionManager**

`MeetNow.Recording.Core/Recording/SessionManager.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Recording;

public class ActiveSession
{
    public string SessionId { get; }
    public string SessionDir { get; }

    private int _chunkIndex;
    private readonly string _sessionJsonPath;
    private readonly SessionMetadata _metadata;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ActiveSession(string sessionDir, SessionMetadata metadata)
    {
        SessionDir = sessionDir;
        SessionId = metadata.SessionId;
        _metadata = metadata;
        _sessionJsonPath = Path.Combine(sessionDir, "session.json");
    }

    public int NextChunkIndex()
    {
        _chunkIndex++;
        _metadata.ChunkCount = _chunkIndex;
        SaveMetadata();
        return _chunkIndex;
    }

    public void Complete()
    {
        _metadata.Status = SessionStatus.Completed;
        _metadata.EndTimeUtc = DateTime.UtcNow;
        _metadata.ChunkCount = _chunkIndex;
        SaveMetadata();
    }

    private void SaveMetadata()
    {
        var json = JsonSerializer.Serialize(_metadata, JsonOptions);
        File.WriteAllText(_sessionJsonPath, json);
    }
}

public class SessionManager
{
    private readonly RecorderConfig _config;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public SessionManager(RecorderConfig config)
    {
        _config = config;
    }

    public bool ShouldStartNewSession(DateTime? lastChunkTimeUtc)
    {
        if (lastChunkTimeUtc == null)
            return true;

        var gap = DateTime.UtcNow - lastChunkTimeUtc.Value;
        return gap.TotalMinutes >= _config.SessionGapMinutes;
    }

    public ActiveSession StartNewSession(string loopbackDevice, string micDevice)
    {
        var sessionId = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
        var sessionDir = Path.Combine(_config.OutputDir, sessionId);

        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(Path.Combine(sessionDir, "chunks"));
        Directory.CreateDirectory(Path.Combine(sessionDir, "transcripts"));

        var metadata = new SessionMetadata
        {
            SessionId = sessionId,
            StartTimeUtc = DateTime.UtcNow,
            Status = SessionStatus.Recording,
            CaptureDevices = new CaptureDeviceInfo
            {
                Loopback = loopbackDevice,
                Microphone = micDevice
            },
            Config = new SessionConfig
            {
                VadMode = _config.VadAggressiveness,
                SilenceTimeoutMs = _config.SilenceTimeoutMs,
                MaxChunkDurationMs = _config.MaxChunkDurationMs
            }
        };

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(Path.Combine(sessionDir, "session.json"), json);

        return new ActiveSession(sessionDir, metadata);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~SessionManagerTests" -v normal
```

Expected: All 8 tests pass.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Core/Recording/SessionManager.cs MeetNow.Recording.Core.Tests/Recording/SessionManagerTests.cs
git commit -m "feat: add SessionManager with session lifecycle management

Creates session folders, writes session.json, tracks chunk count.
Session gap detection for automatic session boundaries."
```

---

### Task 9: ChunkWriter with Tests (TDD)

**Files:**
- Create: `MeetNow.Recording.Core/Recording/ChunkWriter.cs`
- Create: `MeetNow.Recording.Core.Tests/Recording/ChunkWriterTests.cs`

- [ ] **Step 1: Write failing tests**

`MeetNow.Recording.Core.Tests/Recording/ChunkWriterTests.cs`:

```csharp
using System.Text.Json;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Audio;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recording.Core.Tests.Recording;

public class ChunkWriterTests : IDisposable
{
    private readonly string _chunksDir;

    public ChunkWriterTests()
    {
        _chunksDir = Path.Combine(Path.GetTempPath(), "MeetNowTest_" + Guid.NewGuid().ToString("N")[..8], "chunks");
        Directory.CreateDirectory(_chunksDir);
    }

    public void Dispose()
    {
        var parent = Directory.GetParent(_chunksDir)!.FullName;
        if (Directory.Exists(parent))
            Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public void WriteChunk_CreatesLoopbackAndMicWavFiles()
    {
        var writer = new ChunkWriter(_chunksDir);
        var loopback = GenerateSilence(16000); // 1 second
        var mic = GenerateSilence(16000);

        writer.WriteChunk(
            chunkIndex: 1,
            sessionId: "test-session",
            loopbackSamples: loopback,
            micSamples: mic,
            startTimeUtc: DateTime.UtcNow.AddSeconds(-1),
            endTimeUtc: DateTime.UtcNow,
            speechFrames: 30,
            totalFrames: 33,
            splitReason: SplitReason.SilenceTimeout);

        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001_loopback.wav")));
        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001_mic.wav")));
        Assert.True(File.Exists(Path.Combine(_chunksDir, "chunk_001.json")));
    }

    [Fact]
    public void WriteChunk_JsonHasCorrectMetadata()
    {
        var writer = new ChunkWriter(_chunksDir);
        var start = new DateTime(2026, 3, 27, 14, 32, 5, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 27, 14, 32, 47, DateTimeKind.Utc);

        writer.WriteChunk(
            chunkIndex: 3,
            sessionId: "2026-03-27_14-32-05",
            loopbackSamples: GenerateSilence(16000),
            micSamples: GenerateSilence(16000),
            startTimeUtc: start,
            endTimeUtc: end,
            speechFrames: 1287,
            totalFrames: 1426,
            splitReason: SplitReason.SilenceTimeout);

        var json = File.ReadAllText(Path.Combine(_chunksDir, "chunk_003.json"));
        var meta = JsonSerializer.Deserialize<ChunkMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.Equal(3, meta.ChunkIndex);
        Assert.Equal("2026-03-27_14-32-05", meta.SessionId);
        Assert.Equal(ChunkStatus.PendingTranscription, meta.Status);
        Assert.Equal(SplitReason.SilenceTimeout, meta.SplitReason);
        Assert.Equal(1287, meta.VadStats.SpeechFrames);
        Assert.Equal(1426, meta.VadStats.TotalFrames);
        Assert.Equal("chunk_003_loopback.wav", meta.LoopbackFile);
        Assert.Equal("chunk_003_mic.wav", meta.MicFile);
    }

    [Fact]
    public void WriteChunk_WavFilesAreValidPcm()
    {
        var writer = new ChunkWriter(_chunksDir);
        short[] samples = [100, -200, 300, -400, 500];
        var padded = new short[16000]; // 1 second
        samples.CopyTo(padded, 0);

        writer.WriteChunk(1, "test", padded, padded,
            DateTime.UtcNow, DateTime.UtcNow, 1, 1, SplitReason.SilenceTimeout);

        // Read WAV header to verify format
        using var reader = new NAudio.Wave.WaveFileReader(Path.Combine(_chunksDir, "chunk_001_loopback.wav"));
        Assert.Equal(16000, reader.WaveFormat.SampleRate);
        Assert.Equal(16, reader.WaveFormat.BitsPerSample);
        Assert.Equal(1, reader.WaveFormat.Channels);
    }

    private static short[] GenerateSilence(int sampleCount)
    {
        return new short[sampleCount];
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~ChunkWriterTests" --no-restore
```

Expected: Build failure — `ChunkWriter` does not exist.

- [ ] **Step 3: Implement ChunkWriter**

`MeetNow.Recording.Core/Recording/ChunkWriter.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core.Audio;
using NAudio.Wave;

namespace MeetNow.Recording.Core.Recording;

public class ChunkWriter
{
    private readonly string _chunksDir;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChunkWriter(string chunksDir)
    {
        _chunksDir = chunksDir;
    }

    public void WriteChunk(
        int chunkIndex,
        string sessionId,
        short[] loopbackSamples,
        short[] micSamples,
        DateTime startTimeUtc,
        DateTime endTimeUtc,
        int speechFrames,
        int totalFrames,
        SplitReason splitReason)
    {
        var indexStr = chunkIndex.ToString("D3");
        var loopbackFile = $"chunk_{indexStr}_loopback.wav";
        var micFile = $"chunk_{indexStr}_mic.wav";
        var metaFile = $"chunk_{indexStr}.json";

        // Write WAV files first (before metadata — metadata signals readiness)
        WriteWav(Path.Combine(_chunksDir, loopbackFile), loopbackSamples);
        WriteWav(Path.Combine(_chunksDir, micFile), micSamples);

        // Write metadata JSON last — this is the signal to the transcriber
        var metadata = new ChunkMetadata
        {
            ChunkIndex = chunkIndex,
            SessionId = sessionId,
            StartTimeUtc = startTimeUtc,
            EndTimeUtc = endTimeUtc,
            DurationSeconds = (endTimeUtc - startTimeUtc).TotalSeconds,
            LoopbackFile = loopbackFile,
            MicFile = micFile,
            VadStats = new VadStats
            {
                SpeechFrames = speechFrames,
                TotalFrames = totalFrames
            },
            SplitReason = splitReason,
            Status = ChunkStatus.PendingTranscription
        };

        var json = JsonSerializer.Serialize(metadata, JsonOptions);
        File.WriteAllText(Path.Combine(_chunksDir, metaFile), json);
    }

    private static void WriteWav(string path, short[] samples)
    {
        using var writer = new WaveFileWriter(path, AudioFormat.WaveFormat);
        // Convert short[] to byte[] for WaveFileWriter
        var bytes = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        writer.Write(bytes, 0, bytes.Length);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
dotnet test MeetNow.Recording.Core.Tests --filter "FullyQualifiedName~ChunkWriterTests" -v normal
```

Expected: All 3 tests pass.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Core/Recording/ChunkWriter.cs MeetNow.Recording.Core.Tests/Recording/ChunkWriterTests.cs
git commit -m "feat: add ChunkWriter for WAV + metadata output

Writes dual-channel WAV files (16kHz mono 16-bit PCM).
Writes chunk JSON metadata with PendingTranscription status.
WAV files written before JSON — JSON signals readiness to transcriber."
```

---

### Task 10: Audio Capture Streams

**Files:**
- Create: `MeetNow.Recording.Core/Audio/LoopbackCaptureStream.cs`
- Create: `MeetNow.Recording.Core/Audio/MicrophoneCaptureStream.cs`
- Create: `MeetNow.Recording.Core/Audio/DualChannelCapture.cs`

These wrap NAudio's WASAPI APIs and resample to 16kHz mono. They cannot be unit-tested without audio hardware, so no test files in this task.

- [ ] **Step 1: Create LoopbackCaptureStream**

`MeetNow.Recording.Core/Audio/LoopbackCaptureStream.cs`:

```csharp
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Captures system audio output via WASAPI loopback,
/// resamples to 16kHz mono 16-bit PCM, and delivers frames via callback.
/// </summary>
public class LoopbackCaptureStream : IDisposable
{
    private WasapiLoopbackCapture? _capture;
    private WaveFormat? _sourceFormat;

    public event Action<short[]>? OnSamplesAvailable;
    public string DeviceName { get; private set; } = "";

    public void Start()
    {
        _capture = new WasapiLoopbackCapture();
        _sourceFormat = _capture.WaveFormat;
        DeviceName = _capture.CaptureDeviceFriendlyName ?? "Unknown";

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _sourceFormat == null)
            return;

        var samples = ConvertToMono16kHz(e.Buffer, e.BytesRecorded, _sourceFormat);
        if (samples.Length > 0)
            OnSamplesAvailable?.Invoke(samples);
    }

    internal static short[] ConvertToMono16kHz(byte[] buffer, int bytesRecorded, WaveFormat sourceFormat)
    {
        // Source is typically 48kHz/32-bit float/stereo from WASAPI
        int sourceSamples = bytesRecorded / (sourceFormat.BitsPerSample / 8);
        int sourceChannels = sourceFormat.Channels;
        int sourceFrames = sourceSamples / sourceChannels;

        // Downsample ratio
        double ratio = (double)AudioFormat.SampleRate / sourceFormat.SampleRate;
        int outputFrames = (int)(sourceFrames * ratio);

        var output = new short[outputFrames];

        for (int i = 0; i < outputFrames; i++)
        {
            int srcIndex = (int)(i / ratio);
            if (srcIndex >= sourceFrames) srcIndex = sourceFrames - 1;

            float sample = 0;
            // Average all channels to mono
            for (int ch = 0; ch < sourceChannels; ch++)
            {
                int sampleIndex = srcIndex * sourceChannels + ch;
                if (sourceFormat.BitsPerSample == 32 && sourceFormat.Encoding == WaveFormatEncoding.IeeeFloat)
                {
                    int byteOffset = sampleIndex * 4;
                    if (byteOffset + 4 <= bytesRecorded)
                        sample += BitConverter.ToSingle(buffer, byteOffset);
                }
                else if (sourceFormat.BitsPerSample == 16)
                {
                    int byteOffset = sampleIndex * 2;
                    if (byteOffset + 2 <= bytesRecorded)
                        sample += BitConverter.ToInt16(buffer, byteOffset) / 32768f;
                }
            }
            sample /= sourceChannels;

            // Clamp and convert to 16-bit
            sample = Math.Clamp(sample, -1f, 1f);
            output[i] = (short)(sample * 32767);
        }

        return output;
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
```

- [ ] **Step 2: Create MicrophoneCaptureStream**

`MeetNow.Recording.Core/Audio/MicrophoneCaptureStream.cs`:

```csharp
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Captures microphone audio via WASAPI,
/// resamples to 16kHz mono 16-bit PCM, and delivers frames via callback.
/// </summary>
public class MicrophoneCaptureStream : IDisposable
{
    private WasapiCapture? _capture;
    private WaveFormat? _sourceFormat;

    public event Action<short[]>? OnSamplesAvailable;
    public string DeviceName { get; private set; } = "";

    public void Start()
    {
        var enumerator = new MMDeviceEnumerator();
        var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        DeviceName = device.FriendlyName;

        _capture = new WasapiCapture(device);
        _sourceFormat = _capture.WaveFormat;

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    public void Stop()
    {
        _capture?.StopRecording();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || _sourceFormat == null)
            return;

        // Reuse the same conversion logic as loopback
        var samples = LoopbackCaptureStream.ConvertToMono16kHz(e.Buffer, e.BytesRecorded, _sourceFormat);
        if (samples.Length > 0)
            OnSamplesAvailable?.Invoke(samples);
    }

    public void Dispose()
    {
        _capture?.StopRecording();
        _capture?.Dispose();
    }
}
```

- [ ] **Step 3: Create DualChannelCapture**

`MeetNow.Recording.Core/Audio/DualChannelCapture.cs`:

```csharp
using MeetNow.Recording.Core.Config;

namespace MeetNow.Recording.Core.Audio;

/// <summary>
/// Orchestrates loopback + mic capture streams.
/// Feeds samples into ring buffers and delivers VAD-sized frames via callback.
/// </summary>
public class DualChannelCapture : IDisposable
{
    private readonly LoopbackCaptureStream _loopback = new();
    private readonly MicrophoneCaptureStream _mic = new();
    private readonly RingBuffer _loopbackPreBuffer;
    private readonly RingBuffer _micPreBuffer;

    private readonly List<short> _loopbackAccumulator = [];
    private readonly List<short> _micAccumulator = [];
    private readonly object _lock = new();

    /// <summary>
    /// Fired for each VAD frame (480 samples / 30ms).
    /// loopbackFrame and micFrame are the frame samples.
    /// </summary>
    public event Action<short[], short[]>? OnFrameAvailable;

    public string LoopbackDeviceName => _loopback.DeviceName;
    public string MicDeviceName => _mic.DeviceName;

    public DualChannelCapture(RecorderConfig config)
    {
        int preBufferSamples = AudioFormat.PreBufferSamples(config.PreBufferSeconds);
        _loopbackPreBuffer = new RingBuffer(preBufferSamples);
        _micPreBuffer = new RingBuffer(preBufferSamples);

        _loopback.OnSamplesAvailable += OnLoopbackSamples;
        _mic.OnSamplesAvailable += OnMicSamples;
    }

    /// <summary>
    /// Drains the pre-buffers and returns the accumulated samples up to this point.
    /// Called when transitioning from IDLE to RECORDING.
    /// </summary>
    public (short[] loopback, short[] mic) DrainPreBuffers()
    {
        lock (_lock)
        {
            return (_loopbackPreBuffer.Drain(), _micPreBuffer.Drain());
        }
    }

    public void Start()
    {
        _loopback.Start();
        _mic.Start();
    }

    public void Stop()
    {
        _loopback.Stop();
        _mic.Stop();
    }

    private void OnLoopbackSamples(short[] samples)
    {
        lock (_lock)
        {
            _loopbackPreBuffer.Write(samples);
            _loopbackAccumulator.AddRange(samples);
            TryEmitFrames();
        }
    }

    private void OnMicSamples(short[] samples)
    {
        lock (_lock)
        {
            _micPreBuffer.Write(samples);
            _micAccumulator.AddRange(samples);
        }
    }

    private void TryEmitFrames()
    {
        // Emit frames driven by loopback timing.
        // If mic has fewer samples, pad with silence to keep channels aligned.
        while (_loopbackAccumulator.Count >= AudioFormat.VadFrameSamples)
        {
            var loopbackFrame = _loopbackAccumulator.GetRange(0, AudioFormat.VadFrameSamples).ToArray();
            _loopbackAccumulator.RemoveRange(0, AudioFormat.VadFrameSamples);

            short[] micFrame;
            if (_micAccumulator.Count >= AudioFormat.VadFrameSamples)
            {
                micFrame = _micAccumulator.GetRange(0, AudioFormat.VadFrameSamples).ToArray();
                _micAccumulator.RemoveRange(0, AudioFormat.VadFrameSamples);
            }
            else
            {
                // Pad with silence if mic is behind
                micFrame = new short[AudioFormat.VadFrameSamples];
            }

            OnFrameAvailable?.Invoke(loopbackFrame, micFrame);
        }
    }

    public void Dispose()
    {
        _loopback.Dispose();
        _mic.Dispose();
    }
}
```

- [ ] **Step 4: Verify build**

```bash
dotnet build MeetNow.Recording.Core/MeetNow.Recording.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add MeetNow.Recording.Core/Audio/LoopbackCaptureStream.cs MeetNow.Recording.Core/Audio/MicrophoneCaptureStream.cs MeetNow.Recording.Core/Audio/DualChannelCapture.cs
git commit -m "feat: add WASAPI capture streams and DualChannelCapture

LoopbackCaptureStream: system audio via WasapiLoopbackCapture
MicrophoneCaptureStream: mic via WasapiCapture
Both resample to 16kHz mono 16-bit PCM.
DualChannelCapture: orchestrates both, emits aligned VAD frames."
```

---

### Task 11: DeviceMonitor

**Files:**
- Create: `MeetNow.Recording.Core/DeviceMonitor.cs`

- [ ] **Step 1: Implement DeviceMonitor**

`MeetNow.Recording.Core/DeviceMonitor.cs`:

```csharp
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetNow.Recording.Core;

/// <summary>
/// Watches for audio device changes (connect/disconnect/default change).
/// Fires OnDefaultDeviceChanged when the default render or capture device changes.
/// </summary>
public class DeviceMonitor : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;

    public event Action<DataFlow, string>? OnDefaultDeviceChanged;

    public DeviceMonitor()
    {
        _enumerator = new MMDeviceEnumerator();
        _client = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        _enumerator.Dispose();
    }

    private class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceMonitor _owner;
        public NotificationClient(DeviceMonitor owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia || role == Role.Communications)
            {
                _owner.OnDefaultDeviceChanged?.Invoke(flow, defaultDeviceId);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
```

- [ ] **Step 2: Verify build**

```bash
dotnet build MeetNow.Recording.Core/MeetNow.Recording.Core.csproj
```

Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add MeetNow.Recording.Core/DeviceMonitor.cs
git commit -m "feat: add DeviceMonitor for audio device change detection

Uses IMMNotificationClient to detect default device changes.
Fires OnDefaultDeviceChanged for render and capture device switches."
```

---

### Task 12: Recorder Host — RecorderService, TranscriberProcessManager, Program.cs

**Files:**
- Create: `MeetNow.Recorder/RecorderService.cs`
- Create: `MeetNow.Recorder/TranscriberProcessManager.cs`
- Create: `MeetNow.Recorder/Program.cs`
- Create: `MeetNow.Recorder/appsettings.json`

- [ ] **Step 1: Create appsettings.json**

`MeetNow.Recorder/appsettings.json`:

```json
{
  "recording": {
    "outputDir": "%LOCALAPPDATA%\\MeetNow\\Recordings"
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

- [ ] **Step 2: Create TranscriberProcessManager**

`MeetNow.Recorder/TranscriberProcessManager.cs`:

```csharp
using System.Diagnostics;
using MeetNow.Recording.Core.Config;
using Serilog;

namespace MeetNow.Recorder;

public class TranscriberProcessManager : IDisposable
{
    private readonly RecorderConfig _config;
    private readonly string _transcriberDir;
    private Process? _process;
    private int _restartCount;
    private const int MaxRestarts = 5;
    private const int RestartDelayBaseMs = 2000;

    public TranscriberProcessManager(RecorderConfig config)
    {
        _config = config;
        // Transcriber lives alongside the recorder executable
        var exeDir = AppContext.BaseDirectory;
        _transcriberDir = Path.Combine(exeDir, "MeetNow.Recorder.Transcriber");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _restartCount < MaxRestarts)
        {
            try
            {
                StartProcess();
                await _process!.WaitForExitAsync(ct);

                if (ct.IsCancellationRequested)
                    break;

                Log.Warning("Transcriber process exited with code {ExitCode}. Restart {N}/{Max}",
                    _process.ExitCode, _restartCount + 1, MaxRestarts);

                _restartCount++;
                var delay = RestartDelayBaseMs * (1 << Math.Min(_restartCount, 5));
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to start transcriber process");
                _restartCount++;
                await Task.Delay(RestartDelayBaseMs * _restartCount, ct);
            }
        }
    }

    private void StartProcess()
    {
        var args = new List<string>
        {
            "-m", "transcriber",
            "--model", _config.TranscriberModel,
            "--watch-dir", _config.OutputDir,
            "--device", _config.TranscriberDevice,
            "--poll-interval", _config.TranscriberPollIntervalSeconds.ToString()
        };

        if (_config.TranscriberLanguage != null)
        {
            args.Add("--language");
            args.Add(_config.TranscriberLanguage);
        }

        var psi = new ProcessStartInfo
        {
            FileName = _config.PythonPath,
            WorkingDirectory = _transcriberDir,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        _process = Process.Start(psi);

        if (_process != null)
        {
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                    Log.Information("[Transcriber] {Line}", e.Data);
            };
            _process.BeginErrorReadLine();
            Log.Information("Transcriber started (PID {Pid})", _process.Id);
        }
    }

    public void SignalStop()
    {
        if (_process == null || _process.HasExited)
            return;

        // Write stop flag
        var stopFlag = Path.Combine(_config.OutputDir, "stop.flag");
        File.WriteAllText(stopFlag, "stop");
        Log.Information("Stop flag written, waiting for transcriber to finish...");
    }

    public void Dispose()
    {
        if (_process != null && !_process.HasExited)
        {
            SignalStop();
            if (!_process.WaitForExit(5000))
            {
                Log.Warning("Transcriber did not stop gracefully, killing...");
                _process.Kill(entireProcessTree: true);
            }
            _process.Dispose();
        }

        // Clean up stop flag
        var stopFlag = Path.Combine(_config.OutputDir, "stop.flag");
        if (File.Exists(stopFlag))
            File.Delete(stopFlag);
    }
}
```

- [ ] **Step 3: Create RecorderService**

`MeetNow.Recorder/RecorderService.cs`:

```csharp
using MeetNow.Recording.Contracts;
using MeetNow.Recording.Core;
using MeetNow.Recording.Core.Audio;
using MeetNow.Recording.Core.Config;
using MeetNow.Recording.Core.Recording;
using MeetNow.Recording.Core.Vad;
using NAudio.CoreAudioApi;
using Serilog;

namespace MeetNow.Recorder;

public class RecorderService
{
    private readonly RecorderConfig _config;
    private DualChannelCapture? _capture;
    private IVoiceActivityDetector? _loopbackVad;
    private IVoiceActivityDetector? _micVad;
    private RecordingStateMachine? _stateMachine;
    private SessionManager? _sessionManager;
    private ActiveSession? _activeSession;
    private ChunkWriter? _chunkWriter;
    private ChunkTimeline? _timeline;
    private DeviceMonitor? _deviceMonitor;
    private TranscriberProcessManager? _transcriber;

    private readonly List<short> _chunkLoopbackSamples = [];
    private readonly List<short> _chunkMicSamples = [];
    private DateTime? _lastChunkTimeUtc;

    public RecorderService(RecorderConfig config)
    {
        _config = config;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        // Ensure output directory exists
        Directory.CreateDirectory(_config.OutputDir);

        // Check disk space
        CheckDiskSpace();

        // Initialize components
        _sessionManager = new SessionManager(_config);
        _timeline = new ChunkTimeline();

        _loopbackVad = new WebRtcVoiceActivityDetector(_config.VadAggressiveness);
        _micVad = new WebRtcVoiceActivityDetector(_config.VadAggressiveness);

        _stateMachine = new RecordingStateMachine(_config);
        _stateMachine.OnRecordingStarted += OnRecordingStarted;
        _stateMachine.OnFlush += OnFlush;
        _stateMachine.OnDiscard += OnDiscard;

        _capture = new DualChannelCapture(_config);
        _capture.OnFrameAvailable += OnFrameAvailable;

        _deviceMonitor = new DeviceMonitor();
        _deviceMonitor.OnDefaultDeviceChanged += OnDeviceChanged;

        // Start transcriber
        _transcriber = new TranscriberProcessManager(_config);
        var transcriberTask = _transcriber.StartAsync(ct);

        // Start capture
        _capture.Start();
        Log.Information("Recorder started. Loopback: {Lb}, Mic: {Mic}",
            _capture.LoopbackDeviceName, _capture.MicDeviceName);

        UpdateConsoleTitle("IDLE");

        // Periodic disk space check
        using var diskTimer = new PeriodicTimer(TimeSpan.FromMinutes(10));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await diskTimer.WaitForNextTickAsync(ct);
                CheckDiskSpace();
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown
        }

        Log.Information("Shutting down...");

        // Flush current chunk if recording
        _stateMachine.ForceFlush(SplitReason.ManualStop);

        // Complete active session
        _activeSession?.Complete();

        // Stop capture
        _capture.Stop();

        // Stop transcriber
        _transcriber.Dispose();

        // Cleanup
        _capture.Dispose();
        _deviceMonitor.Dispose();
        _loopbackVad.Dispose();
        _micVad.Dispose();

        Log.Information("Recorder stopped.");
    }

    private void OnFrameAvailable(short[] loopbackFrame, short[] micFrame)
    {
        bool loopbackSpeech = _loopbackVad!.IsSpeech(loopbackFrame);
        bool micSpeech = _micVad!.IsSpeech(micFrame);

        // Always accumulate into chunk buffers if recording
        if (_stateMachine!.State != RecorderState.Idle)
        {
            _chunkLoopbackSamples.AddRange(loopbackFrame);
            _chunkMicSamples.AddRange(micFrame);
            _timeline!.AddFrames(1);
            if (loopbackSpeech) _timeline.AddSpeechFrame();
        }

        _stateMachine.ProcessFrame(loopbackSpeech, micSpeech);
    }

    private void OnRecordingStarted()
    {
        // Start new session if needed
        if (_activeSession == null || _sessionManager!.ShouldStartNewSession(_lastChunkTimeUtc))
        {
            _activeSession?.Complete();
            _activeSession = _sessionManager!.StartNewSession(
                _capture!.LoopbackDeviceName, _capture.MicDeviceName);
            Log.Information("New session: {Id}", _activeSession.SessionId);
        }

        var chunkIndex = _activeSession.NextChunkIndex();
        _chunkWriter = new ChunkWriter(Path.Combine(_activeSession.SessionDir, "chunks"));
        _timeline!.Start(chunkIndex);

        // Drain pre-buffers and prepend to chunk
        var (preLoopback, preMic) = _capture!.DrainPreBuffers();
        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _chunkLoopbackSamples.AddRange(preLoopback);
        _chunkMicSamples.AddRange(preMic);

        UpdateConsoleTitle($"RECORDING chunk {chunkIndex:D3}");
        Log.Information("Recording started: chunk {Index}", chunkIndex);
    }

    private void OnFlush(SplitReason reason)
    {
        _timeline!.Stop();

        _chunkWriter!.WriteChunk(
            chunkIndex: _timeline.ChunkIndex,
            sessionId: _activeSession!.SessionId,
            loopbackSamples: [.. _chunkLoopbackSamples],
            micSamples: [.. _chunkMicSamples],
            startTimeUtc: _timeline.StartTimeUtc,
            endTimeUtc: _timeline.EndTimeUtc,
            speechFrames: _timeline.SpeechFrames,
            totalFrames: _timeline.TotalFrames,
            splitReason: reason);

        _lastChunkTimeUtc = DateTime.UtcNow;

        Log.Information("Chunk {Index} flushed ({Duration:F1}s, reason: {Reason})",
            _timeline.ChunkIndex, _timeline.DurationSeconds, reason);

        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _timeline.Reset();

        UpdateConsoleTitle("IDLE");
    }

    private void OnDiscard()
    {
        Log.Debug("Chunk discarded (too short)");
        _chunkLoopbackSamples.Clear();
        _chunkMicSamples.Clear();
        _timeline!.Reset();
        UpdateConsoleTitle("IDLE");
    }

    private void OnDeviceChanged(DataFlow flow, string deviceId)
    {
        Log.Warning("Audio device changed: {Flow} → {DeviceId}", flow, deviceId);

        if (_stateMachine!.State != RecorderState.Idle)
        {
            _stateMachine.ForceFlush(SplitReason.DeviceChange);
        }

        // Reinitialize capture
        _capture!.Stop();
        _capture.Dispose();

        _capture = new DualChannelCapture(_config);
        _capture.OnFrameAvailable += OnFrameAvailable;
        _capture.Start();

        Log.Information("Capture reinitialized. Loopback: {Lb}, Mic: {Mic}",
            _capture.LoopbackDeviceName, _capture.MicDeviceName);
    }

    private void CheckDiskSpace()
    {
        var drive = new DriveInfo(Path.GetPathRoot(_config.OutputDir) ?? "C:");
        var freeMb = drive.AvailableFreeSpace / (1024 * 1024);

        if (freeMb < _config.CriticalFreeDiskMb)
        {
            Log.Error("CRITICAL: Only {FreeMb}MB free. Stopping recording.", freeMb);
            // The state machine will be force-flushed on shutdown
        }
        else if (freeMb < _config.MinFreeDiskMb)
        {
            Log.Warning("Low disk space: {FreeMb}MB free", freeMb);
        }
    }

    private static void UpdateConsoleTitle(string status)
    {
        try
        {
            Console.Title = $"MeetNow Recorder [{status}]";
        }
        catch
        {
            // Console.Title can throw in non-console contexts
        }
    }
}
```

- [ ] **Step 4: Create Program.cs**

`MeetNow.Recorder/Program.cs`:

```csharp
using MeetNow.Recording.Core.Config;
using MeetNow.Recorder;
using Microsoft.Extensions.Configuration;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .WriteTo.File(
        Path.Combine(Path.GetTempPath(), "MeetNow.Recorder.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7)
    .CreateLogger();

try
{
    Log.Information("MeetNow Recorder starting...");

    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false)
        .Build();

    var config = new RecorderConfig();

    var recording = configuration.GetSection("recording");
    if (recording["outputDir"] is string outputDir)
        config.OutputDir = Environment.ExpandEnvironmentVariables(outputDir);

    var vad = configuration.GetSection("vad");
    if (int.TryParse(vad["aggressiveness"], out var agg)) config.VadAggressiveness = agg;
    if (int.TryParse(vad["hysteresisRequired"], out var hr)) config.HysteresisRequired = hr;
    if (int.TryParse(vad["hysteresisWindow"], out var hw)) config.HysteresisWindow = hw;

    var chunking = configuration.GetSection("chunking");
    if (int.TryParse(chunking["preBufferSeconds"], out var pb)) config.PreBufferSeconds = pb;
    if (int.TryParse(chunking["silenceTimeoutMs"], out var st)) config.SilenceTimeoutMs = st;
    if (int.TryParse(chunking["minChunkDurationMs"], out var minC)) config.MinChunkDurationMs = minC;
    if (int.TryParse(chunking["maxChunkDurationMs"], out var maxC)) config.MaxChunkDurationMs = maxC;
    if (int.TryParse(chunking["maxChunkGraceMs"], out var mg)) config.MaxChunkGraceMs = mg;
    if (int.TryParse(chunking["micKeepaliveMs"], out var mk)) config.MicKeepaliveMs = mk;
    if (int.TryParse(chunking["sessionGapMinutes"], out var sg)) config.SessionGapMinutes = sg;

    var transcription = configuration.GetSection("transcription");
    if (transcription["pythonPath"] is string pp) config.PythonPath = pp;
    if (transcription["model"] is string model) config.TranscriberModel = model;
    if (transcription["device"] is string device) config.TranscriberDevice = device;
    config.TranscriberLanguage = transcription["language"];
    if (int.TryParse(transcription["pollIntervalSeconds"], out var pi)) config.TranscriberPollIntervalSeconds = pi;

    var storage = configuration.GetSection("storage");
    if (bool.TryParse(storage["archiveToFlac"], out var af)) config.ArchiveToFlac = af;
    if (bool.TryParse(storage["deleteWavAfterArchive"], out var dw)) config.DeleteWavAfterArchive = dw;
    if (int.TryParse(storage["minFreeDiskMb"], out var mfd)) config.MinFreeDiskMb = mfd;
    if (int.TryParse(storage["criticalFreeDiskMb"], out var cfd)) config.CriticalFreeDiskMb = cfd;

    var service = new RecorderService(config);
    var cts = new CancellationTokenSource();

    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        Log.Information("Ctrl+C received, shutting down...");
        cts.Cancel();
    };

    await service.RunAsync(cts.Token);
}
catch (Exception ex)
{
    Log.Fatal(ex, "Recorder crashed");
}
finally
{
    Log.CloseAndFlush();
}
```

- [ ] **Step 5: Verify build**

```bash
dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj
```

Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add MeetNow.Recorder/
git commit -m "feat: add Recorder console host with service orchestration

Program.cs: thin entry point with config loading and Serilog setup
RecorderService: wires capture + VAD + state machine + chunk writing
TranscriberProcessManager: starts/monitors/restarts Python sidecar
appsettings.json: all configurable parameters per spec"
```

---

### Task 13: Python Transcriber — Project Structure and Config

**Files:**
- Create: `MeetNow.Recorder.Transcriber/requirements.txt`
- Create: `MeetNow.Recorder.Transcriber/transcriber/__init__.py`
- Create: `MeetNow.Recorder.Transcriber/transcriber/config.py`
- Create: `MeetNow.Recorder.Transcriber/transcriber/__main__.py`

- [ ] **Step 1: Create requirements.txt**

`MeetNow.Recorder.Transcriber/requirements.txt`:

```
faster-whisper>=1.0.0
```

- [ ] **Step 2: Create package init and config**

`MeetNow.Recorder.Transcriber/transcriber/__init__.py`:

```python
"""MeetNow Recorder Transcriber — local Faster-Whisper transcription sidecar."""
```

`MeetNow.Recorder.Transcriber/transcriber/config.py`:

```python
"""CLI argument parsing and configuration defaults."""

import argparse
from dataclasses import dataclass
from pathlib import Path


@dataclass
class TranscriberConfig:
    watch_dir: Path
    model: str = "small"
    device: str = "cuda"
    language: str | None = None
    poll_interval: int = 2
    max_retries: int = 3
    abandon_timeout_minutes: int = 10


def parse_args() -> TranscriberConfig:
    parser = argparse.ArgumentParser(description="MeetNow Transcriber")
    parser.add_argument("--watch-dir", type=Path, required=True,
                        help="Base recordings directory to watch")
    parser.add_argument("--model", default="small",
                        help="Faster-Whisper model size (default: small)")
    parser.add_argument("--device", default="cuda",
                        help="Device for inference: cuda or cpu (default: cuda)")
    parser.add_argument("--language", default=None,
                        help="Language code (default: auto-detect)")
    parser.add_argument("--poll-interval", type=int, default=2,
                        help="Seconds between poll cycles (default: 2)")
    args = parser.parse_args()

    return TranscriberConfig(
        watch_dir=args.watch_dir,
        model=args.model,
        device=args.device,
        language=args.language,
        poll_interval=args.poll_interval,
    )
```

- [ ] **Step 3: Create __main__.py entry point**

`MeetNow.Recorder.Transcriber/transcriber/__main__.py`:

```python
"""Entry point: python -m transcriber"""

import sys
import time
import logging

from .config import parse_args
from .watcher import Watcher

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%H:%M:%S",
    stream=sys.stderr,
)
log = logging.getLogger(__name__)


def main() -> None:
    config = parse_args()
    log.info("Transcriber starting — model=%s, device=%s, watching=%s",
             config.model, config.device, config.watch_dir)

    watcher = Watcher(config)

    try:
        watcher.run()
    except KeyboardInterrupt:
        log.info("Interrupted, shutting down.")
    except Exception:
        log.exception("Transcriber crashed")
        sys.exit(1)


main()
```

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recorder.Transcriber/
git commit -m "feat: add Python transcriber project structure

Package skeleton with CLI config parsing.
Entry point: python -m transcriber --watch-dir ... --model small"
```

---

### Task 14: Python Transcriber — Watcher with Atomic Claim

**Files:**
- Create: `MeetNow.Recorder.Transcriber/transcriber/watcher.py`

- [ ] **Step 1: Implement watcher with atomic claim protocol**

`MeetNow.Recorder.Transcriber/transcriber/watcher.py`:

```python
"""Polls for pending chunks and dispatches transcription."""

import json
import logging
import msvcrt
import time
from datetime import datetime, timezone
from pathlib import Path

from .config import TranscriberConfig
from .transcribe import TranscriptionEngine
from .merger import build_session_transcript

log = logging.getLogger(__name__)


class Watcher:
    def __init__(self, config: TranscriberConfig) -> None:
        self.config = config
        self.engine = TranscriptionEngine(config)

    def run(self) -> None:
        stop_flag = self.config.watch_dir / "stop.flag"

        while True:
            if stop_flag.exists():
                log.info("Stop flag detected, finishing.")
                stop_flag.unlink(missing_ok=True)
                break

            self._poll_cycle()
            time.sleep(self.config.poll_interval)

    def _poll_cycle(self) -> None:
        if not self.config.watch_dir.exists():
            return

        for session_dir in sorted(self.config.watch_dir.iterdir()):
            if not session_dir.is_dir():
                continue

            chunks_dir = session_dir / "chunks"
            if not chunks_dir.exists():
                continue

            for chunk_json in sorted(chunks_dir.glob("chunk_*.json")):
                # Skip files that look like transcript output
                if "_loopback" in chunk_json.stem or "_mic" in chunk_json.stem:
                    continue

                self._process_chunk(chunk_json, session_dir)

            # Check if session is complete and all chunks are done
            self._check_session_complete(session_dir)

    def _process_chunk(self, chunk_json: Path, session_dir: Path) -> None:
        try:
            meta = self._read_json(chunk_json)
        except (json.JSONDecodeError, OSError) as e:
            log.debug("Skipping unreadable %s: %s", chunk_json.name, e)
            return

        status = meta.get("status", "")

        if status == "pending_transcription":
            if self._claim_chunk(chunk_json, meta):
                self._transcribe_chunk(chunk_json, meta, session_dir)

        elif status == "transcribing":
            self._reclaim_if_abandoned(chunk_json, meta)

    def _claim_chunk(self, chunk_json: Path, meta: dict) -> bool:
        """Atomic claim: file-locked read-check-write."""
        try:
            with open(chunk_json, "r+") as f:
                # Lock the file
                msvcrt.locking(f.fileno(), msvcrt.LK_NBLCK, 1)
                try:
                    # Re-read under lock
                    f.seek(0)
                    current = json.load(f)
                    if current.get("status") != "pending_transcription":
                        return False  # Someone else claimed it

                    # Write claim
                    current["status"] = "transcribing"
                    current["claimedAtUtc"] = datetime.now(timezone.utc).isoformat()
                    f.seek(0)
                    f.truncate()
                    json.dump(current, f, indent=2)
                    return True
                finally:
                    f.seek(0)
                    msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
        except OSError:
            return False  # File locked by another process

    def _reclaim_if_abandoned(self, chunk_json: Path, meta: dict) -> None:
        claimed_at = meta.get("claimedAtUtc")
        if not claimed_at:
            return

        try:
            claimed_time = datetime.fromisoformat(claimed_at)
            age_minutes = (datetime.now(timezone.utc) - claimed_time).total_seconds() / 60
            if age_minutes > self.config.abandon_timeout_minutes:
                log.warning("Re-claiming abandoned chunk %s (claimed %s min ago)",
                            chunk_json.name, f"{age_minutes:.0f}")
                # Reset to pending so next cycle picks it up
                meta["status"] = "pending_transcription"
                meta.pop("claimedAtUtc", None)
                self._write_json(chunk_json, meta)
        except (ValueError, TypeError):
            pass

    def _transcribe_chunk(self, chunk_json: Path, meta: dict, session_dir: Path) -> None:
        chunk_index = meta.get("chunkIndex", 0)
        chunks_dir = chunk_json.parent
        transcripts_dir = session_dir / "transcripts"
        transcripts_dir.mkdir(exist_ok=True)

        loopback_wav = chunks_dir / meta.get("loopbackFile", "")
        mic_wav = chunks_dir / meta.get("micFile", "")

        log.info("Transcribing chunk %03d...", chunk_index)

        retries = 0
        last_error = ""
        success = False

        while retries < self.config.max_retries:
            try:
                timestamp_base = meta.get("startTimeUtc", datetime.now(timezone.utc).isoformat())

                # Transcribe loopback
                if loopback_wav.exists():
                    result = self.engine.transcribe(loopback_wav, chunk_index, "loopback", timestamp_base)
                    out_path = transcripts_dir / f"chunk_{chunk_index:03d}_loopback.json"
                    self._write_json(out_path, result)

                # Transcribe mic
                if mic_wav.exists():
                    result = self.engine.transcribe(mic_wav, chunk_index, "mic", timestamp_base)
                    out_path = transcripts_dir / f"chunk_{chunk_index:03d}_mic.json"
                    self._write_json(out_path, result)

                success = True
                break

            except Exception as e:
                retries += 1
                last_error = str(e)
                log.warning("Transcription attempt %d/%d failed for chunk %03d: %s",
                            retries, self.config.max_retries, chunk_index, e)
                time.sleep(1)

        # Update chunk status
        meta = self._read_json(chunk_json)
        if success:
            meta["status"] = "transcribed"
            meta.pop("claimedAtUtc", None)
            meta.pop("error", None)
            log.info("Chunk %03d transcribed.", chunk_index)
        else:
            meta["status"] = "failed"
            meta["error"] = last_error
            log.error("Chunk %03d failed after %d retries: %s", chunk_index, retries, last_error)

        self._write_json(chunk_json, meta)

    def _check_session_complete(self, session_dir: Path) -> None:
        session_json = session_dir / "session.json"
        if not session_json.exists():
            return

        session = self._read_json(session_json)
        if session.get("status") != "completed":
            return

        # Check if merged transcript already exists
        merged_path = session_dir / "session_transcript.json"
        if merged_path.exists():
            return

        # Check all chunks are in terminal state
        chunks_dir = session_dir / "chunks"
        for chunk_file in chunks_dir.glob("chunk_*.json"):
            if "_loopback" in chunk_file.stem or "_mic" in chunk_file.stem:
                continue
            chunk = self._read_json(chunk_file)
            status = chunk.get("status", "")
            if status not in ("transcribed", "failed"):
                return  # Still processing

        # All chunks done — build merged transcript
        log.info("Building merged transcript for session %s", session_dir.name)
        build_session_transcript(session_dir)

        # Update session status
        session["status"] = "transcribed"
        self._write_json(session_json, session)

    @staticmethod
    def _read_json(path: Path) -> dict:
        return json.loads(path.read_text(encoding="utf-8"))

    @staticmethod
    def _write_json(path: Path, data: dict) -> None:
        path.write_text(json.dumps(data, indent=2, default=str), encoding="utf-8")
```

- [ ] **Step 2: Commit**

```bash
git add MeetNow.Recorder.Transcriber/transcriber/watcher.py
git commit -m "feat: add Python watcher with atomic claim protocol

Polls for pending_transcription chunks, claims with file lock,
dispatches transcription, handles retries and abandonment recovery.
Triggers session transcript merge when all chunks complete."
```

---

### Task 15: Python Transcriber — Transcription Engine

**Files:**
- Create: `MeetNow.Recorder.Transcriber/transcriber/transcribe.py`

- [ ] **Step 1: Implement transcription engine**

`MeetNow.Recorder.Transcriber/transcriber/transcribe.py`:

```python
"""Faster-Whisper transcription engine."""

import logging
import time
from pathlib import Path

from faster_whisper import WhisperModel

from .config import TranscriberConfig

log = logging.getLogger(__name__)


class TranscriptionEngine:
    def __init__(self, config: TranscriberConfig) -> None:
        self.config = config
        self._model: WhisperModel | None = None

    @property
    def model(self) -> WhisperModel:
        """Lazy-load model on first use (keeps GPU memory free until needed)."""
        if self._model is None:
            log.info("Loading Faster-Whisper model '%s' on %s...",
                     self.config.model, self.config.device)
            compute_type = "float16" if self.config.device == "cuda" else "int8"
            self._model = WhisperModel(
                self.config.model,
                device=self.config.device,
                compute_type=compute_type,
            )
            log.info("Model loaded.")
        return self._model

    def transcribe(
        self,
        wav_path: Path,
        chunk_index: int,
        channel: str,
        timestamp_utc_base: str,
    ) -> dict:
        start_time = time.monotonic()

        segments_iter, info = self.model.transcribe(
            str(wav_path),
            language=self.config.language,
            word_timestamps=True,
            vad_filter=False,  # We already did VAD in C#
        )

        segments = []
        for seg in segments_iter:
            words = []
            if seg.words:
                for w in seg.words:
                    words.append({
                        "word": w.word.strip(),
                        "start": round(w.start, 3),
                        "end": round(w.end, 3),
                        "probability": round(w.probability, 3),
                    })

            segments.append({
                "start": round(seg.start, 3),
                "end": round(seg.end, 3),
                "text": seg.text.strip(),
                "words": words,
            })

        elapsed = time.monotonic() - start_time
        log.info("  %s/%03d: lang=%s (%.0f%%), %d segments, %.1fs",
                 channel, chunk_index, info.language,
                 info.language_probability * 100, len(segments), elapsed)

        return {
            "chunkIndex": chunk_index,
            "channel": channel,
            "language": info.language,
            "languageProbability": round(info.language_probability, 3),
            "segments": segments,
            "transcriptionTimeSeconds": round(elapsed, 2),
            "modelName": self.config.model,
            "timestampUtcBase": timestamp_utc_base,
        }
```

- [ ] **Step 2: Commit**

```bash
git add MeetNow.Recorder.Transcriber/transcriber/transcribe.py
git commit -m "feat: add Faster-Whisper transcription engine

Lazy model loading, word-level timestamps, configurable model/device.
Returns structured transcript dict matching Contracts schema."
```

---

### Task 16: Python Transcriber — Session Merger

**Files:**
- Create: `MeetNow.Recorder.Transcriber/transcriber/merger.py`

- [ ] **Step 1: Implement session transcript merger**

`MeetNow.Recorder.Transcriber/transcriber/merger.py`:

```python
"""Builds merged session transcript from per-chunk transcripts."""

import json
import logging
from datetime import datetime, timedelta
from pathlib import Path

log = logging.getLogger(__name__)


def build_session_transcript(session_dir: Path) -> None:
    session_json = session_dir / "session.json"
    session = json.loads(session_json.read_text(encoding="utf-8"))

    chunks_dir = session_dir / "chunks"
    transcripts_dir = session_dir / "transcripts"

    loopback_segments: list[dict] = []
    mic_segments: list[dict] = []

    # Collect all chunk transcripts in order
    chunk_files = sorted(chunks_dir.glob("chunk_*.json"))
    for chunk_file in chunk_files:
        if "_loopback" in chunk_file.stem or "_mic" in chunk_file.stem:
            continue

        chunk = json.loads(chunk_file.read_text(encoding="utf-8"))
        if chunk.get("status") != "transcribed":
            continue

        chunk_index = chunk["chunkIndex"]
        base_time_str = chunk["startTimeUtc"]
        base_time = _parse_iso(base_time_str)

        # Load loopback transcript
        lb_path = transcripts_dir / f"chunk_{chunk_index:03d}_loopback.json"
        if lb_path.exists():
            transcript = json.loads(lb_path.read_text(encoding="utf-8"))
            for seg in transcript.get("segments", []):
                seg_start = base_time + timedelta(seconds=seg["start"])
                seg_end = base_time + timedelta(seconds=seg["end"])
                loopback_segments.append({
                    "start": seg_start.isoformat(),
                    "end": seg_end.isoformat(),
                    "speaker": "other",
                    "text": seg["text"],
                })

        # Load mic transcript
        mic_path = transcripts_dir / f"chunk_{chunk_index:03d}_mic.json"
        if mic_path.exists():
            transcript = json.loads(mic_path.read_text(encoding="utf-8"))
            for seg in transcript.get("segments", []):
                seg_start = base_time + timedelta(seconds=seg["start"])
                seg_end = base_time + timedelta(seconds=seg["end"])
                mic_segments.append({
                    "start": seg_start.isoformat(),
                    "end": seg_end.isoformat(),
                    "speaker": "me",
                    "text": seg["text"],
                })

    # Merge and sort by start time
    merged = sorted(loopback_segments + mic_segments, key=lambda s: s["start"])

    # Compute duration
    session_start = _parse_iso(session["startTimeUtc"])
    session_end = _parse_iso(session.get("endTimeUtc", session["startTimeUtc"]))
    duration = session_end - session_start

    result = {
        "sessionId": session["sessionId"],
        "duration": str(duration).split(".")[0],  # HH:MM:SS
        "channels": {
            "loopback": loopback_segments,
            "mic": mic_segments,
        },
        "merged": merged,
    }

    output_path = session_dir / "session_transcript.json"
    output_path.write_text(json.dumps(result, indent=2, default=str), encoding="utf-8")
    log.info("Session transcript written: %s (%d segments)", output_path.name, len(merged))


def _parse_iso(s: str) -> datetime:
    """Parse ISO format datetime, handling various formats."""
    s = s.rstrip("Z")
    if "+" in s:
        s = s.split("+")[0]
    return datetime.fromisoformat(s)
```

- [ ] **Step 2: Commit**

```bash
git add MeetNow.Recorder.Transcriber/transcriber/merger.py
git commit -m "feat: add session transcript merger

Collects per-chunk transcripts, converts relative timestamps to absolute,
merges loopback (other) + mic (me) segments sorted by time.
Outputs session_transcript.json with merged timeline."
```

---

### Task 17: End-to-End Smoke Test

**Files:** None created — manual verification.

- [ ] **Step 1: Run all unit tests**

```bash
cd C:/Users/Boris.Kudriashov/Source/repos/MeetNow
dotnet test MeetNow.Recording.Core.Tests -v normal
```

Expected: All tests pass (RingBuffer: 10, StateMachine: 14, ChunkTimeline: 6, SessionManager: 8, ChunkWriter: 3 = 41 total).

- [ ] **Step 2: Build the recorder**

```bash
dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj -c Release
```

Expected: Build succeeded.

- [ ] **Step 3: Verify Python transcriber starts**

```bash
cd C:/Users/Boris.Kudriashov/Source/repos/MeetNow/MeetNow.Recorder.Transcriber
python -m transcriber --help
```

Expected: Prints argument help text with --watch-dir, --model, --device, etc.

- [ ] **Step 4: Run the recorder briefly**

```bash
cd C:/Users/Boris.Kudriashov/Source/repos/MeetNow
dotnet run --project MeetNow.Recorder
```

Expected: Console shows `MeetNow Recorder starting...`, device names, and enters IDLE state. Ctrl+C stops cleanly. Fix any runtime errors.

- [ ] **Step 5: Commit any fixes**

If fixes were needed, commit them:

```bash
git add -A
git commit -m "fix: address issues found during smoke testing"
```

- [ ] **Step 6: Full solution build verification**

```bash
dotnet build MeetNow.sln
```

Expected: All projects build successfully including the existing MeetNow WPF app.
