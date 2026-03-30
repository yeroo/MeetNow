# Recorder Tray UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Convert MeetNow.Recorder from a console app to a WPF tray application with recording status indicators, a master/detail status dashboard, audio playback with transcript sync, and session management.

**Architecture:** The existing RecorderService stays headless — new events expose state/audio levels to a WPF layer. App.xaml hosts the tray icon and status window. A RecorderViewModel bridges service events to UI via INotifyPropertyChanged, marshalling to the UI thread. SessionViewModel reads session/chunk/transcript data from disk, with FileSystemWatcher for live updates.

**Tech Stack:** WPF (.NET 9), Hardcodet.NotifyIcon.Wpf, NAudio (playback), Serilog, INotifyPropertyChanged (no MVVM framework)

---

## File Structure

### New Files (MeetNow.Recorder)

| File | Responsibility |
|------|---------------|
| `App.xaml` / `App.xaml.cs` | WPF app lifecycle, single-instance Mutex, Serilog init, RecorderService startup, tray icon hosting |
| `TrayIcon.xaml` | Hardcodet TaskbarIcon with context menu, icon overlay logic |
| `StatusWindow.xaml` / `StatusWindow.xaml.cs` | Master/detail dashboard window |
| `ViewModels/RecorderViewModel.cs` | Bridges RecorderService events → UI properties (state, audio levels, sessions) |
| `ViewModels/SessionViewModel.cs` | Per-session data: status, chunks, transcript, transcription progress |
| `ViewModels/BaseViewModel.cs` | INotifyPropertyChanged base class |
| `Controls/AudioLevelMeter.xaml` / `.xaml.cs` | Reusable audio level bar control |
| `Controls/TranscriptView.xaml` / `.xaml.cs` | Transcript display with clickable segments and highlight sync |
| `Audio/SessionAudioPlayer.cs` | NAudio-based player: load chunks, seek, channel toggle (loopback/mic/both) |
| `Icons/icon-idle.ico` | Normal tray icon |
| `Icons/icon-recording.ico` | Tray icon with red dot overlay badge |
| `Icons/icon-transcribing.ico` | Tray icon with orange dot overlay badge |

### Modified Files

| File | Changes |
|------|---------|
| `MeetNow.Recorder.csproj` | OutputType → WinExe, add UseWPF, add Hardcodet.NotifyIcon.Wpf |
| `RecorderService.cs` | Add StateChanged, AudioLevelChanged, ChunkFlushed, SessionStarted/Completed events. Remove UpdateConsoleTitle. |
| `Program.cs` | Remove — replaced by App.xaml.cs entry point |

### Unchanged Files

| File | Why unchanged |
|------|--------------|
| `MeetNow.Recording.Core/*` | All recording logic stays as-is |
| `TranscriberProcessManager.cs` | Transcriber launch/stop stays as-is |
| `appsettings.json` | Config loading moves to App.xaml.cs but format unchanged |

---

## Task 1: Convert Project to WPF and Add Tray Icon

**Files:**
- Modify: `MeetNow.Recorder/MeetNow.Recorder.csproj`
- Create: `MeetNow.Recorder/App.xaml`
- Create: `MeetNow.Recorder/App.xaml.cs`
- Create: `MeetNow.Recorder/TrayIcon.xaml`
- Create: `MeetNow.Recorder/Icons/icon-idle.ico`
- Create: `MeetNow.Recorder/Icons/icon-recording.ico`
- Create: `MeetNow.Recorder/Icons/icon-transcribing.ico`
- Delete: `MeetNow.Recorder/Program.cs`

- [ ] **Step 1: Update csproj to WPF**

Replace the full content of `MeetNow.Recorder/MeetNow.Recorder.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows10.0.19041.0</TargetFramework>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <SelfContained>true</SelfContained>
    <PublishSingleFile>true</PublishSingleFile>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>Icons\icon-idle.ico</ApplicationIcon>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\MeetNow.Recording.Core\MeetNow.Recording.Core.csproj" />
    <ProjectReference Include="..\MeetNow.Recording.Contracts\MeetNow.Recording.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Hardcodet.NotifyIcon.Wpf" Version="1.1.0" />
    <PackageReference Include="Microsoft.Extensions.Configuration.Json" Version="8.0.1" />
    <PackageReference Include="Serilog" Version="3.1.1" />
    <PackageReference Include="Serilog.Sinks.Console" Version="5.0.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="5.0.0" />
    <PackageReference Include="WebRtcVadSharp" Version="1.3.0" />
  </ItemGroup>
  <ItemGroup>
    <Content Include="appsettings.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </Content>
    <Resource Include="Icons\icon-idle.ico" />
    <Resource Include="Icons\icon-recording.ico" />
    <Resource Include="Icons\icon-transcribing.ico" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Generate tray icons**

Create three `.ico` files. Use the existing `MeetNow/Icons/MeetNow.ico` as the base. For `icon-recording.ico`, add a small red filled circle in the bottom-right quadrant. For `icon-transcribing.ico`, add a small orange filled circle in the bottom-right quadrant. For `icon-idle.ico`, use the base icon as-is (copy it).

Use a programmatic approach — write a small C# script or use `System.Drawing` in a one-off dotnet-script to generate the icons from the base, or create them manually with an icon editor. The icons should be 16x16 and 32x32 multi-resolution ICO files.

If icon generation is complex, start with copies of the base icon for all three and add a TODO comment in `App.xaml.cs` noting that icon overlays need visual polish. The tray icon logic should reference the correct file names regardless.

- [ ] **Step 3: Create App.xaml**

Create `MeetNow.Recorder/App.xaml`:

```xml
<Application x:Class="MeetNow.Recorder.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             ShutdownMode="OnExplicitShutdown">
    <Application.Resources>
    </Application.Resources>
</Application>
```

Note: `ShutdownMode="OnExplicitShutdown"` so closing the status window doesn't exit the app. No `StartupUri` — we start headless with just the tray icon.

- [ ] **Step 4: Create App.xaml.cs**

Create `MeetNow.Recorder/App.xaml.cs`:

```csharp
using System.IO;
using System.Threading;
using System.Windows;
using MeetNow.Recording.Core.Config;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace MeetNow.Recorder;

public partial class App : Application
{
    private static Mutex? _mutex;
    private RecorderService? _service;
    private CancellationTokenSource? _cts;
    private TrayIcon? _trayIcon;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single instance
        _mutex = new Mutex(true, "MeetNowRecorder_SingleInstance_A1B2C3", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MeetNow Recorder is already running.", "MeetNow Recorder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Logging
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(Path.GetTempPath(), "MeetNow.Recorder.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("MeetNow Recorder starting...");

        // Configuration
        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var config = LoadConfig(configuration);

        // Start recorder service
        _service = new RecorderService(config);
        _cts = new CancellationTokenSource();

        // Create tray icon
        _trayIcon = new TrayIcon(_service, _cts);

        // Run service on background thread
        _ = Task.Run(() => _service.RunAsync(_cts.Token));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        _trayIcon?.Dispose();
        Log.Information("MeetNow Recorder stopped.");
        Log.CloseAndFlush();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private static RecorderConfig LoadConfig(IConfigurationRoot configuration)
    {
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
        if (bool.TryParse(transcription["enabled"], out var te)) config.TranscriberEnabled = te;
        if (transcription["pythonPath"] is string pp) config.PythonPath = pp;
        if (transcription["model"] is string model) config.TranscriberModel = model;
        if (transcription["device"] is string device) config.TranscriberDevice = device;
        if (transcription["language"] is string lang && lang.Length > 0) config.TranscriberLanguage = lang;
        if (int.TryParse(transcription["pollIntervalSeconds"], out var pi)) config.TranscriberPollIntervalSeconds = pi;

        var storage = configuration.GetSection("storage");
        if (bool.TryParse(storage["archiveToFlac"], out var af)) config.ArchiveToFlac = af;
        if (bool.TryParse(storage["deleteWavAfterArchive"], out var dw)) config.DeleteWavAfterArchive = dw;
        if (int.TryParse(storage["minFreeDiskMb"], out var mfd)) config.MinFreeDiskMb = mfd;
        if (int.TryParse(storage["criticalFreeDiskMb"], out var cfd)) config.CriticalFreeDiskMb = cfd;

        return config;
    }
}
```

- [ ] **Step 5: Create TrayIcon.xaml**

Create `MeetNow.Recorder/TrayIcon.xaml`:

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:tb="http://www.hardcodet.net/taskbar">
    <!-- TrayIcon is created in code-behind for lifecycle control -->
</ResourceDictionary>
```

Create `MeetNow.Recorder/TrayIcon.xaml.cs` — actually, since the tray icon is best managed in code (dynamic icon swapping, tooltip updates), make this a plain C# class instead of XAML:

Create `MeetNow.Recorder/TrayIcon.cs`:

```csharp
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recorder;

public class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly RecorderService _service;
    private readonly CancellationTokenSource _cts;
    private StatusWindow? _statusWindow;

    private readonly Icon _idleIcon;
    private readonly Icon _recordingIcon;
    private readonly Icon _transcribingIcon;

    public TrayIcon(RecorderService service, CancellationTokenSource cts)
    {
        _service = service;
        _cts = cts;

        _idleIcon = LoadIcon("Icons/icon-idle.ico");
        _recordingIcon = LoadIcon("Icons/icon-recording.ico");
        _transcribingIcon = LoadIcon("Icons/icon-transcribing.ico");

        _taskbarIcon = new TaskbarIcon
        {
            Icon = _idleIcon,
            ToolTipText = "MeetNow Recorder — Idle",
            ContextMenu = BuildContextMenu(),
        };

        _taskbarIcon.TrayMouseDoubleClick += OnDoubleClick;
        _service.StateChanged += OnStateChanged;
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var statusItem = new MenuItem { Header = "Open Status Window", FontWeight = FontWeights.Bold };
        statusItem.Click += (_, _) => ShowStatusWindow();
        menu.Items.Add(statusItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += OnExitClick;
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OnDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowStatusWindow();
    }

    private void ShowStatusWindow()
    {
        if (_statusWindow == null || !_statusWindow.IsLoaded)
        {
            _statusWindow = new StatusWindow(_service);
        }

        _statusWindow.Show();
        _statusWindow.Activate();
    }

    private void OnStateChanged(RecorderState state)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case RecorderState.Recording:
                case RecorderState.MicKeepalive:
                case RecorderState.Draining:
                    _taskbarIcon.Icon = _recordingIcon;
                    _taskbarIcon.ToolTipText = "MeetNow Recorder — Recording";
                    break;
                default:
                    _taskbarIcon.Icon = _idleIcon;
                    _taskbarIcon.ToolTipText = "MeetNow Recorder — Idle";
                    break;
            }
        });
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (_service.IsRecording)
        {
            var result = MessageBox.Show(
                "Recording is in progress. Are you sure you want to exit?",
                "MeetNow Recorder", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        _cts.Cancel();
        Application.Current.Shutdown();
    }

    private static Icon LoadIcon(string relativePath)
    {
        var path = Path.Combine(AppContext.BaseDirectory, relativePath);
        return new Icon(path);
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        _taskbarIcon.Dispose();
        _idleIcon.Dispose();
        _recordingIcon.Dispose();
        _transcribingIcon.Dispose();
    }
}
```

- [ ] **Step 6: Delete Program.cs**

Delete `MeetNow.Recorder/Program.cs`. Its config-loading logic is now in `App.xaml.cs.LoadConfig()` and its entry point is replaced by the WPF `App` class.

- [ ] **Step 7: Build and verify tray icon appears**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds. If icon files don't exist yet, the build will succeed but runtime will fail to load icons — that's addressed in Step 2.

- [ ] **Step 8: Commit**

```bash
git add MeetNow.Recorder/
git commit -m "feat(recorder): convert to WPF tray app with system tray icon

Replace console app with WPF application. Add Hardcodet.NotifyIcon.Wpf
tray icon with idle/recording/transcribing states, context menu, and
double-click to open status window."
```

---

## Task 2: Add RecorderService Events

**Files:**
- Modify: `MeetNow.Recorder/RecorderService.cs`

- [ ] **Step 1: Add new events and IsRecording property**

Add these members to `RecorderService` class after the existing field declarations:

```csharp
// UI events
public event Action<RecorderState>? StateChanged;
public event Action<float, float>? AudioLevelChanged;
public event Action<int, string>? ChunkFlushed;  // chunkIndex, sessionId
public event Action<string>? SessionStarted;      // sessionId
public event Action<string>? SessionCompleted;     // sessionId

public bool IsRecording => _stateMachine?.State is RecorderState.Recording
    or RecorderState.MicKeepalive or RecorderState.Draining;
```

- [ ] **Step 2: Compute and fire audio levels in OnFrameAvailable**

Replace the `OnFrameAvailable` method:

```csharp
private void OnFrameAvailable(short[] loopbackFrame, short[] micFrame)
{
    bool loopbackSpeech = _loopbackVad!.IsSpeech(loopbackFrame);
    bool micSpeech = _micVad!.IsSpeech(micFrame);

    // Compute RMS audio levels for UI meters
    AudioLevelChanged?.Invoke(ComputeRms(loopbackFrame), ComputeRms(micFrame));

    // Always accumulate into chunk buffers if recording
    if (_stateMachine!.State != RecorderState.Idle)
    {
        _chunkLoopbackSamples.AddRange(loopbackFrame);
        _chunkMicSamples.AddRange(micFrame);
        _timeline!.AddFrames(1);
        if (loopbackSpeech) _timeline.AddSpeechFrame();
    }

    var prevState = _stateMachine.State;
    _stateMachine.ProcessFrame(loopbackSpeech, micSpeech);
    if (_stateMachine.State != prevState)
        StateChanged?.Invoke(_stateMachine.State);
}

private static float ComputeRms(short[] samples)
{
    if (samples.Length == 0) return 0f;
    double sum = 0;
    for (int i = 0; i < samples.Length; i++)
        sum += (double)samples[i] * samples[i];
    return (float)Math.Sqrt(sum / samples.Length) / short.MaxValue;
}
```

- [ ] **Step 3: Fire session and chunk events**

In `OnRecordingStarted`, after `Log.Information("New session: {Id}", ...)`:

```csharp
SessionStarted?.Invoke(_activeSession.SessionId);
```

In `OnFlush`, after `Log.Information("Chunk {Index} flushed ...")`:

```csharp
ChunkFlushed?.Invoke(_timeline.ChunkIndex, _activeSession!.SessionId);
```

In the shutdown section of `RunAsync`, after `_activeSession?.Complete()`:

```csharp
if (_activeSession != null)
    SessionCompleted?.Invoke(_activeSession.SessionId);
```

- [ ] **Step 4: Remove UpdateConsoleTitle**

Delete the `UpdateConsoleTitle` method and all calls to it (in `OnRecordingStarted`, `OnFlush`, `OnDiscard`, and `RunAsync`).

- [ ] **Step 5: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds with no errors.

- [ ] **Step 6: Commit**

```bash
git add MeetNow.Recorder/RecorderService.cs
git commit -m "feat(recorder): add UI events for state, audio levels, sessions

Add StateChanged, AudioLevelChanged, ChunkFlushed, SessionStarted,
SessionCompleted events. Remove console title updates."
```

---

## Task 3: Create BaseViewModel and SessionViewModel

**Files:**
- Create: `MeetNow.Recorder/ViewModels/BaseViewModel.cs`
- Create: `MeetNow.Recorder/ViewModels/SessionViewModel.cs`

- [ ] **Step 1: Create BaseViewModel**

Create `MeetNow.Recorder/ViewModels/BaseViewModel.cs`:

```csharp
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MeetNow.Recorder.ViewModels;

public abstract class BaseViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
```

- [ ] **Step 2: Create SessionViewModel**

Create `MeetNow.Recorder/ViewModels/SessionViewModel.cs`:

```csharp
using System.IO;
using System.Text.Json;

namespace MeetNow.Recorder.ViewModels;

public class SessionViewModel : BaseViewModel
{
    public string SessionId { get; }
    public string SessionDir { get; }
    public DateTime StartTimeUtc { get; }

    private string _status = "unknown";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private TimeSpan _duration;
    public TimeSpan Duration { get => _duration; set => SetField(ref _duration, value); }

    private int _totalChunks;
    public int TotalChunks { get => _totalChunks; set => SetField(ref _totalChunks, value); }

    private int _transcribedChunks;
    public int TranscribedChunks { get => _transcribedChunks; set => SetField(ref _transcribedChunks, value); }

    private int _failedChunks;
    public int FailedChunks { get => _failedChunks; set => SetField(ref _failedChunks, value); }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set => SetField(ref _errorMessage, value); }

    public bool HasTranscript => File.Exists(Path.Combine(SessionDir, "transcript.txt"));

    public string TranscriptPath => Path.Combine(SessionDir, "transcript.txt");

    public SessionViewModel(string sessionDir)
    {
        SessionDir = sessionDir;
        SessionId = Path.GetFileName(sessionDir);

        var sessionJson = Path.Combine(sessionDir, "session.json");
        if (File.Exists(sessionJson))
        {
            var json = JsonDocument.Parse(File.ReadAllText(sessionJson));
            var root = json.RootElement;

            if (root.TryGetProperty("startTimeUtc", out var start))
                StartTimeUtc = DateTime.Parse(start.GetString()!);

            if (root.TryGetProperty("status", out var status))
                Status = status.GetString() ?? "unknown";

            if (root.TryGetProperty("endTimeUtc", out var end) && end.ValueKind != JsonValueKind.Null)
            {
                var endTime = DateTime.Parse(end.GetString()!);
                Duration = endTime - StartTimeUtc;
            }

            if (root.TryGetProperty("chunkCount", out var cc))
                TotalChunks = cc.GetInt32();
        }

        RefreshTranscriptionProgress();
    }

    public void Refresh()
    {
        var sessionJson = Path.Combine(SessionDir, "session.json");
        if (!File.Exists(sessionJson)) return;

        var json = JsonDocument.Parse(File.ReadAllText(sessionJson));
        var root = json.RootElement;

        if (root.TryGetProperty("status", out var status))
            Status = status.GetString() ?? "unknown";

        if (root.TryGetProperty("endTimeUtc", out var end) && end.ValueKind != JsonValueKind.Null)
        {
            var endTime = DateTime.Parse(end.GetString()!);
            Duration = endTime - StartTimeUtc;
        }

        if (root.TryGetProperty("chunkCount", out var cc))
            TotalChunks = cc.GetInt32();

        RefreshTranscriptionProgress();
        OnPropertyChanged(nameof(HasTranscript));
    }

    private void RefreshTranscriptionProgress()
    {
        var chunksDir = Path.Combine(SessionDir, "chunks");
        if (!Directory.Exists(chunksDir)) return;

        int transcribed = 0, failed = 0, total = 0;
        foreach (var file in Directory.GetFiles(chunksDir, "chunk_*.json"))
        {
            if (file.Contains("_loopback") || file.Contains("_mic")) continue;
            total++;
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var s = doc.RootElement.GetProperty("status").GetString();
                if (s == "transcribed") transcribed++;
                else if (s == "failed") failed++;
            }
            catch { }
        }

        TotalChunks = total;
        TranscribedChunks = transcribed;
        FailedChunks = failed;
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recorder/ViewModels/
git commit -m "feat(recorder): add BaseViewModel and SessionViewModel

SessionViewModel reads session.json and chunk metadata to track
recording status, duration, and transcription progress."
```

---

## Task 4: Create RecorderViewModel

**Files:**
- Create: `MeetNow.Recorder/ViewModels/RecorderViewModel.cs`

- [ ] **Step 1: Create RecorderViewModel**

Create `MeetNow.Recorder/ViewModels/RecorderViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recorder.ViewModels;

public class RecorderViewModel : BaseViewModel, IDisposable
{
    private readonly RecorderService _service;
    private readonly FileSystemWatcher _watcher;
    private readonly Dispatcher _dispatcher;
    private readonly string _recordingsDir;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    private SessionViewModel? _selectedSession;
    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set => SetField(ref _selectedSession, value);
    }

    private RecorderState _state = RecorderState.Idle;
    public RecorderState State { get => _state; set => SetField(ref _state, value); }

    private float _loopbackLevel;
    public float LoopbackLevel { get => _loopbackLevel; set => SetField(ref _loopbackLevel, value); }

    private float _micLevel;
    public float MicLevel { get => _micLevel; set => SetField(ref _micLevel, value); }

    private long _freeDiskMb;
    public long FreeDiskMb { get => _freeDiskMb; set => SetField(ref _freeDiskMb, value); }

    public RecorderViewModel(RecorderService service)
    {
        _service = service;
        _dispatcher = Application.Current.Dispatcher;
        _recordingsDir = service.Config.OutputDir;

        // Subscribe to service events
        _service.StateChanged += OnStateChanged;
        _service.AudioLevelChanged += OnAudioLevelChanged;
        _service.SessionStarted += OnSessionStarted;
        _service.SessionCompleted += OnSessionCompleted;
        _service.ChunkFlushed += OnChunkFlushed;

        // Load existing sessions
        LoadExistingSessions();

        // Watch for file changes (new sessions, transcript completions)
        _watcher = new FileSystemWatcher(_recordingsDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;

        // Disk space
        UpdateDiskSpace();
    }

    private void LoadExistingSessions()
    {
        if (!Directory.Exists(_recordingsDir)) return;

        foreach (var dir in Directory.GetDirectories(_recordingsDir)
                     .OrderByDescending(d => d))
        {
            if (File.Exists(Path.Combine(dir, "session.json")))
                Sessions.Add(new SessionViewModel(dir));
        }

        if (Sessions.Count > 0)
            SelectedSession = Sessions[0];
    }

    private void OnStateChanged(RecorderState state)
    {
        _dispatcher.Invoke(() => State = state);
    }

    private void OnAudioLevelChanged(float loopback, float mic)
    {
        // Throttle UI updates — only update if change is visible
        _dispatcher.InvokeAsync(() =>
        {
            LoopbackLevel = loopback;
            MicLevel = mic;
        }, DispatcherPriority.Render);
    }

    private void OnSessionStarted(string sessionId)
    {
        _dispatcher.Invoke(() =>
        {
            var dir = Path.Combine(_recordingsDir, sessionId);
            var session = new SessionViewModel(dir);
            Sessions.Insert(0, session);
            SelectedSession = session;
        });
    }

    private void OnSessionCompleted(string sessionId)
    {
        _dispatcher.Invoke(() =>
        {
            var session = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            session?.Refresh();
        });
    }

    private void OnChunkFlushed(int chunkIndex, string sessionId)
    {
        _dispatcher.Invoke(() =>
        {
            var session = Sessions.FirstOrDefault(s => s.SessionId == sessionId);
            session?.Refresh();
        });
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Refresh session when transcript files appear or session.json changes
        if (!e.Name?.EndsWith(".json") == true) return;

        _dispatcher.InvokeAsync(() =>
        {
            // Find which session this file belongs to
            var parts = e.FullPath.Replace(_recordingsDir, "").TrimStart(Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar);
            if (parts.Length < 1) return;

            var session = Sessions.FirstOrDefault(s => s.SessionId == parts[0]);
            session?.Refresh();
        }, DispatcherPriority.Background);
    }

    public void UpdateDiskSpace()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_recordingsDir) ?? "C:");
            FreeDiskMb = drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch { }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        _service.AudioLevelChanged -= OnAudioLevelChanged;
        _service.SessionStarted -= OnSessionStarted;
        _service.SessionCompleted -= OnSessionCompleted;
        _service.ChunkFlushed -= OnChunkFlushed;
        _watcher.Dispose();
    }
}
```

- [ ] **Step 2: Expose Config on RecorderService**

In `RecorderService.cs`, add a public property for the config so RecorderViewModel can read `OutputDir`:

```csharp
public RecorderConfig Config => _config;
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recorder/ViewModels/RecorderViewModel.cs MeetNow.Recorder/RecorderService.cs
git commit -m "feat(recorder): add RecorderViewModel with live state binding

Bridges RecorderService events to UI. Loads existing sessions,
watches filesystem for transcript updates, tracks audio levels
and disk space."
```

---

## Task 5: Create Audio Level Meter Control

**Files:**
- Create: `MeetNow.Recorder/Controls/AudioLevelMeter.xaml`
- Create: `MeetNow.Recorder/Controls/AudioLevelMeter.xaml.cs`

- [ ] **Step 1: Create AudioLevelMeter XAML**

Create `MeetNow.Recorder/Controls/AudioLevelMeter.xaml`:

```xml
<UserControl x:Class="MeetNow.Recorder.Controls.AudioLevelMeter"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Height="8">
    <Grid>
        <Border Background="#2A2A4A" CornerRadius="3" />
        <Border x:Name="LevelBar" Background="#4CAF50" CornerRadius="3"
                HorizontalAlignment="Left" />
    </Grid>
</UserControl>
```

- [ ] **Step 2: Create AudioLevelMeter code-behind**

Create `MeetNow.Recorder/Controls/AudioLevelMeter.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MeetNow.Recorder.Controls;

public partial class AudioLevelMeter : UserControl
{
    public static readonly DependencyProperty LevelProperty =
        DependencyProperty.Register(nameof(Level), typeof(float), typeof(AudioLevelMeter),
            new PropertyMetadata(0f, OnLevelChanged));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(AudioLevelMeter),
            new PropertyMetadata(""));

    public float Level
    {
        get => (float)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public AudioLevelMeter()
    {
        InitializeComponent();
    }

    private static void OnLevelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var meter = (AudioLevelMeter)d;
        var level = Math.Clamp((float)e.NewValue, 0f, 1f);

        // Scale: 0-1 RMS mapped to visual width. Boost low values for visibility.
        var displayLevel = Math.Min(1.0, level * 5.0);

        meter.LevelBar.Width = meter.ActualWidth * displayLevel;

        // Color: green → yellow → red
        meter.LevelBar.Background = level switch
        {
            > 0.15f => new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36)), // red
            > 0.08f => new SolidColorBrush(Color.FromRgb(0xFF, 0xEB, 0x3B)), // yellow
            _ => new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),       // green
        };
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recorder/Controls/
git commit -m "feat(recorder): add AudioLevelMeter control

Horizontal bar with green/yellow/red coloring based on RMS level.
Bound via Level dependency property."
```

---

## Task 6: Create Status Window (Master/Detail Layout)

**Files:**
- Create: `MeetNow.Recorder/StatusWindow.xaml`
- Create: `MeetNow.Recorder/StatusWindow.xaml.cs`

- [ ] **Step 1: Create StatusWindow XAML**

Create `MeetNow.Recorder/StatusWindow.xaml`:

```xml
<Window x:Class="MeetNow.Recorder.StatusWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:controls="clr-namespace:MeetNow.Recorder.Controls"
        Title="MeetNow Recorder" Width="900" Height="600"
        Background="#1F1F1F" WindowStartupLocation="CenterScreen">
    <Window.Resources>
        <Style TargetType="TextBlock">
            <Setter Property="Foreground" Value="#E0E0E0" />
            <Setter Property="FontFamily" Value="Segoe UI" />
        </Style>
        <Style x:Key="HeaderText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="#E0E0E0" />
            <Setter Property="FontSize" Value="14" />
            <Setter Property="FontWeight" Value="SemiBold" />
        </Style>
        <Style x:Key="SubText" TargetType="TextBlock">
            <Setter Property="Foreground" Value="#888888" />
            <Setter Property="FontSize" Value="11" />
        </Style>
        <Style x:Key="ActionButton" TargetType="Button">
            <Setter Property="Background" Value="#2A2A4A" />
            <Setter Property="Foreground" Value="#E0E0E0" />
            <Setter Property="BorderThickness" Value="0" />
            <Setter Property="Padding" Value="12,6" />
            <Setter Property="Margin" Value="4,0" />
            <Setter Property="Cursor" Value="Hand" />
        </Style>
    </Window.Resources>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="280" MinWidth="200" />
            <ColumnDefinition Width="1" />
            <ColumnDefinition Width="*" />
        </Grid.ColumnDefinitions>

        <!-- LEFT PANE: Session List -->
        <DockPanel Grid.Column="0" Background="#1A1A2E">
            <TextBlock DockPanel.Dock="Top" Text="SESSIONS" Style="{StaticResource SubText}"
                       Margin="12,12,12,8" FontSize="10" />

            <!-- Disk usage at bottom -->
            <Border DockPanel.Dock="Bottom" BorderBrush="#2A2A4A" BorderThickness="0,1,0,0" Padding="12,8">
                <TextBlock x:Name="DiskUsageText" Style="{StaticResource SubText}" Text="Disk: -- GB free" />
            </Border>

            <ListBox x:Name="SessionList" Background="Transparent" BorderThickness="0"
                     SelectionChanged="SessionList_SelectionChanged"
                     HorizontalContentAlignment="Stretch">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="8,6">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="Auto" />
                                <ColumnDefinition Width="*" />
                                <ColumnDefinition Width="Auto" />
                            </Grid.ColumnDefinitions>
                            <TextBlock x:Name="StatusIcon" Grid.Column="0" Margin="0,0,8,0" FontSize="12" />
                            <StackPanel Grid.Column="1">
                                <TextBlock x:Name="SessionDate" FontSize="12" Foreground="#E0E0E0" />
                                <TextBlock x:Name="SessionDuration" FontSize="10" Foreground="#888888" />
                            </StackPanel>
                            <TextBlock x:Name="ProgressText" Grid.Column="2" FontSize="10"
                                       VerticalAlignment="Center" />
                        </Grid>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
        </DockPanel>

        <!-- Splitter -->
        <Border Grid.Column="1" Background="#2A2A4A" />

        <!-- RIGHT PANE: Detail -->
        <DockPanel x:Name="DetailPanel" Grid.Column="2" Margin="16">
            <!-- Header -->
            <StackPanel DockPanel.Dock="Top" Margin="0,0,0,12">
                <StackPanel Orientation="Horizontal">
                    <Ellipse x:Name="RecordingDot" Width="10" Height="10" Fill="#F44336"
                             Margin="0,0,8,0" Visibility="Collapsed" />
                    <TextBlock x:Name="DetailHeader" Style="{StaticResource HeaderText}" Text="No session selected" />
                </StackPanel>
                <TextBlock x:Name="DetailSubHeader" Style="{StaticResource SubText}" Margin="0,4,0,0" />
            </StackPanel>

            <!-- Audio Meters (visible during recording) -->
            <StackPanel x:Name="AudioMetersPanel" DockPanel.Dock="Top" Margin="0,0,0,12" Visibility="Collapsed">
                <TextBlock Text="LOOPBACK" Style="{StaticResource SubText}" FontSize="10" Margin="0,0,0,2" />
                <controls:AudioLevelMeter x:Name="LoopbackMeter" Margin="0,0,0,6" />
                <TextBlock Text="MICROPHONE" Style="{StaticResource SubText}" FontSize="10" Margin="0,0,0,2" />
                <controls:AudioLevelMeter x:Name="MicMeter" />
            </StackPanel>

            <!-- Action buttons (visible for completed sessions) -->
            <StackPanel x:Name="ActionButtonsPanel" DockPanel.Dock="Bottom"
                        Orientation="Horizontal" Margin="0,12,0,0" Visibility="Collapsed">
                <Button Style="{StaticResource ActionButton}" Content="▶ Open Transcript"
                        Click="OpenTranscript_Click" />
                <Button Style="{StaticResource ActionButton}" Content="📁 Open Folder"
                        Click="OpenFolder_Click" />
                <Button Style="{StaticResource ActionButton}" Content="🔄 Retry Transcription"
                        Click="RetryTranscription_Click" />
                <Button x:Name="DeleteButton" Style="{StaticResource ActionButton}" Content="🗑 Delete Session"
                        Click="DeleteSession_Click" />
            </StackPanel>

            <!-- Audio Player (visible for completed sessions) -->
            <Border x:Name="AudioPlayerPanel" DockPanel.Dock="Bottom"
                    Background="#12122A" CornerRadius="4" Padding="8" Margin="0,8,0,0"
                    Visibility="Collapsed">
                <Grid>
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="*" />
                        <ColumnDefinition Width="Auto" />
                        <ColumnDefinition Width="Auto" />
                    </Grid.ColumnDefinitions>
                    <Button x:Name="PlayPauseButton" Grid.Column="0" Content="▶"
                            Style="{StaticResource ActionButton}" Click="PlayPause_Click" Width="36" />
                    <Slider x:Name="SeekSlider" Grid.Column="1" Margin="8,0"
                            VerticalAlignment="Center"
                            ValueChanged="SeekSlider_ValueChanged" />
                    <TextBlock x:Name="PlaybackTimeText" Grid.Column="2" Style="{StaticResource SubText}"
                               VerticalAlignment="Center" Text="00:00 / 00:00" Margin="8,0" />
                    <StackPanel Grid.Column="3" Orientation="Horizontal">
                        <RadioButton x:Name="ChannelLoopback" Content="Loopback" Foreground="#888"
                                     Margin="4,0" IsChecked="True" Checked="Channel_Changed" />
                        <RadioButton x:Name="ChannelMic" Content="Mic" Foreground="#888"
                                     Margin="4,0" Checked="Channel_Changed" />
                        <RadioButton x:Name="ChannelBoth" Content="Both" Foreground="#888"
                                     Margin="4,0" Checked="Channel_Changed" />
                    </StackPanel>
                </Grid>
            </Border>

            <!-- Transcript / Live view -->
            <Border Background="#12122A" CornerRadius="4" Padding="8">
                <ScrollViewer x:Name="TranscriptScroller" VerticalScrollBarVisibility="Auto">
                    <StackPanel x:Name="TranscriptPanel" />
                </ScrollViewer>
            </Border>
        </DockPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Create StatusWindow code-behind**

Create `MeetNow.Recorder/StatusWindow.xaml.cs`:

```csharp
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetNow.Recorder.ViewModels;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recorder;

public partial class StatusWindow : Window
{
    private readonly RecorderViewModel _viewModel;
    private readonly DispatcherTimer _uiTimer;
    private SessionAudioPlayer? _audioPlayer;

    public StatusWindow(RecorderService service)
    {
        InitializeComponent();

        _viewModel = new RecorderViewModel(service);

        SessionList.ItemsSource = _viewModel.Sessions;
        if (_viewModel.Sessions.Count > 0)
            SessionList.SelectedIndex = 0;

        // Bind audio meters
        _viewModel.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(RecorderViewModel.LoopbackLevel):
                    LoopbackMeter.Level = _viewModel.LoopbackLevel;
                    break;
                case nameof(RecorderViewModel.MicLevel):
                    MicMeter.Level = _viewModel.MicLevel;
                    break;
                case nameof(RecorderViewModel.State):
                    UpdateRecordingState(_viewModel.State);
                    break;
                case nameof(RecorderViewModel.FreeDiskMb):
                    DiskUsageText.Text = $"Disk: {_viewModel.FreeDiskMb / 1024.0:F1} GB free";
                    break;
            }
        };

        // Periodic updates
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _uiTimer.Tick += (_, _) => _viewModel.UpdateDiskSpace();
        _uiTimer.Start();

        DiskUsageText.Text = $"Disk: {_viewModel.FreeDiskMb / 1024.0:F1} GB free";
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close
        e.Cancel = true;
        Hide();
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session) return;
        _viewModel.SelectedSession = session;
        ShowSessionDetail(session);
    }

    private void ShowSessionDetail(SessionViewModel session)
    {
        StopAudioPlayer();

        // Header
        DetailHeader.Text = session.SessionId;
        var isRecording = session.Status == "recording";

        RecordingDot.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        AudioMetersPanel.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;

        var isCompleted = session.Status is "completed" or "transcribed";
        ActionButtonsPanel.Visibility = isCompleted || session.Status == "recording"
            ? Visibility.Visible : Visibility.Collapsed;
        AudioPlayerPanel.Visibility = isCompleted ? Visibility.Visible : Visibility.Collapsed;

        // Sub-header
        if (isRecording)
        {
            DetailSubHeader.Text = $"Chunks: {session.TotalChunks}";
        }
        else
        {
            var durationStr = session.Duration.ToString(@"hh\:mm\:ss");
            DetailSubHeader.Text = $"Duration: {durationStr} · {session.TotalChunks} chunks · " +
                                   $"Transcribed: {session.TranscribedChunks}/{session.TotalChunks}";
        }

        // Load transcript
        LoadTranscript(session);

        // Setup audio player for completed sessions
        if (isCompleted)
            SetupAudioPlayer(session);
    }

    private void LoadTranscript(SessionViewModel session)
    {
        TranscriptPanel.Children.Clear();

        var transcriptPath = Path.Combine(session.SessionDir, "transcript.txt");
        if (File.Exists(transcriptPath))
        {
            var lines = File.ReadAllLines(transcriptPath);
            foreach (var line in lines)
            {
                var tb = new TextBlock
                {
                    Text = line,
                    Foreground = GetSpeakerColor(line),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 1, 0, 1),
                    Cursor = Cursors.Hand,
                    Tag = ParseTimestamp(line),
                };
                tb.MouseLeftButtonDown += TranscriptLine_Click;
                TranscriptPanel.Children.Add(tb);
            }
            return;
        }

        // Show chunk placeholders for in-progress sessions
        var chunksDir = Path.Combine(session.SessionDir, "chunks");
        if (!Directory.Exists(chunksDir)) return;

        foreach (var file in Directory.GetFiles(chunksDir, "chunk_*.json")
                     .Where(f => !f.Contains("_loopback") && !f.Contains("_mic"))
                     .OrderBy(f => f))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var index = root.GetProperty("chunkIndex").GetInt32();
                var status = root.GetProperty("status").GetString();
                var duration = root.GetProperty("durationSeconds").GetDouble();

                var color = status switch
                {
                    "transcribed" => "#4CAF50",
                    "transcribing" => "#FF9800",
                    "failed" => "#F44336",
                    _ => "#888888",
                };

                var text = status == "failed" && root.TryGetProperty("error", out var err)
                    ? $"Chunk {index:D3} — {duration:F1}s — FAILED: {err.GetString()}"
                    : $"Chunk {index:D3} — {duration:F1}s — {status}";

                TranscriptPanel.Children.Add(new TextBlock
                {
                    Text = text,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 2),
                });

                // If transcribed, show transcript text below
                var transcriptFile = Path.Combine(session.SessionDir, "transcripts",
                    $"chunk_{index:03d}_loopback.json");
                if (status == "transcribed" && File.Exists(transcriptFile))
                {
                    var tDoc = JsonDocument.Parse(File.ReadAllText(transcriptFile));
                    foreach (var seg in tDoc.RootElement.GetProperty("segments").EnumerateArray())
                    {
                        var segText = seg.GetProperty("text").GetString() ?? "";
                        var start = seg.GetProperty("start").GetDouble();
                        TranscriptPanel.Children.Add(new TextBlock
                        {
                            Text = $"  [{TimeSpan.FromSeconds(start):mm\\:ss}] {segText}",
                            Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0)),
                            FontSize = 11,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(16, 1, 0, 1),
                        });
                    }
                }
            }
            catch { }
        }
    }

    private static Brush GetSpeakerColor(string line)
    {
        if (line.Contains("] Me:")) return new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));
        if (line.Contains("] Person 1:")) return new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6));
        if (line.Contains("] Person 2:")) return new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8));
        if (line.Contains("] Person 3:")) return new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
        if (line.Contains("] Person")) return new SolidColorBrush(Color.FromRgb(0x80, 0xDE, 0xEA));
        if (line.StartsWith("=")) return new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));
        return new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    }

    private static TimeSpan? ParseTimestamp(string line)
    {
        // Parse "[HH:MM:SS]" from transcript lines
        if (line.Length < 10 || line[0] != '[') return null;
        var bracket = line.IndexOf(']');
        if (bracket < 0) return null;
        if (TimeSpan.TryParse(line[1..bracket], out var ts)) return ts;
        return null;
    }

    private void TranscriptLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock { Tag: TimeSpan ts } && _audioPlayer != null)
        {
            _audioPlayer.Seek(ts);
            if (!_audioPlayer.IsPlaying)
                _audioPlayer.Play();
        }
    }

    private void UpdateRecordingState(RecorderState state)
    {
        var isRecording = state is RecorderState.Recording or RecorderState.MicKeepalive or RecorderState.Draining;
        RecordingDot.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;

        // Refresh selected session if it's the active one
        if (_viewModel.SelectedSession?.Status == "recording")
            ShowSessionDetail(_viewModel.SelectedSession);
    }

    #region Audio Player

    private void SetupAudioPlayer(SessionViewModel session)
    {
        _audioPlayer = new SessionAudioPlayer(session.SessionDir);
        SeekSlider.Maximum = _audioPlayer.TotalDuration.TotalSeconds;
        PlaybackTimeText.Text = $"00:00 / {_audioPlayer.TotalDuration:mm\\:ss}";
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer == null) return;

        if (_audioPlayer.IsPlaying)
        {
            _audioPlayer.Pause();
            PlayPauseButton.Content = "▶";
        }
        else
        {
            _audioPlayer.Play();
            PlayPauseButton.Content = "⏸";

            // Start position update timer
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += (_, _) =>
            {
                if (_audioPlayer == null || !_audioPlayer.IsPlaying)
                {
                    timer.Stop();
                    PlayPauseButton.Content = "▶";
                    return;
                }
                SeekSlider.Value = _audioPlayer.Position.TotalSeconds;
                PlaybackTimeText.Text = $"{_audioPlayer.Position:mm\\:ss} / {_audioPlayer.TotalDuration:mm\\:ss}";
                HighlightCurrentSegment(_audioPlayer.Position);
            };
            timer.Start();
        }
    }

    private bool _isSeeking;
    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_audioPlayer == null || _isSeeking) return;
        _isSeeking = true;
        _audioPlayer.Seek(TimeSpan.FromSeconds(e.NewValue));
        _isSeeking = false;
    }

    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (_audioPlayer == null) return;
        var channel = ChannelLoopback.IsChecked == true ? AudioChannel.Loopback
            : ChannelMic.IsChecked == true ? AudioChannel.Mic
            : AudioChannel.Both;
        _audioPlayer.SetChannel(channel);
    }

    private void HighlightCurrentSegment(TimeSpan position)
    {
        foreach (var child in TranscriptPanel.Children)
        {
            if (child is TextBlock tb && tb.Tag is TimeSpan ts)
            {
                tb.Background = Math.Abs((ts - position).TotalSeconds) < 5
                    ? new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF))
                    : Brushes.Transparent;
            }
        }
    }

    private void StopAudioPlayer()
    {
        _audioPlayer?.Dispose();
        _audioPlayer = null;
        PlayPauseButton.Content = "▶";
    }

    #endregion

    #region Actions

    private void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is not { HasTranscript: true } session) return;
        Process.Start(new ProcessStartInfo(session.TranscriptPath) { UseShellExecute = true });
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is not { } session) return;
        Process.Start(new ProcessStartInfo(session.SessionDir) { UseShellExecute = true });
    }

    private void RetryTranscription_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is not { } session) return;

        // Reset failed/transcribing chunks to pending
        var chunksDir = Path.Combine(session.SessionDir, "chunks");
        foreach (var file in Directory.GetFiles(chunksDir, "chunk_*.json")
                     .Where(f => !f.Contains("_loopback") && !f.Contains("_mic")))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var status = doc.RootElement.GetProperty("status").GetString();
                if (status is "failed" or "transcribing")
                {
                    var obj = JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(file))!;
                    obj["status"] = "pending_transcription";
                    obj.Remove("error");
                    obj.Remove("claimedAtUtc");
                    File.WriteAllText(file, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        session.Refresh();
        ShowSessionDetail(session);
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedSession is not { } session) return;

        var result = MessageBox.Show(
            $"Delete session {session.SessionId} and all its recordings?",
            "Delete Session", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        StopAudioPlayer();

        try
        {
            Directory.Delete(session.SessionDir, recursive: true);
            _viewModel.Sessions.Remove(session);
            if (_viewModel.Sessions.Count > 0)
                SessionList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    #endregion
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build will fail because `SessionAudioPlayer` and `AudioChannel` don't exist yet. That's expected — they're created in Task 7.

- [ ] **Step 4: Commit**

```bash
git add MeetNow.Recorder/StatusWindow.xaml MeetNow.Recorder/StatusWindow.xaml.cs
git commit -m "feat(recorder): add StatusWindow with master/detail layout

Session list on left, detail pane on right with audio meters,
transcript view, action buttons, and audio player controls.
Dark theme matching main MeetNow app."
```

---

## Task 7: Create Session Audio Player

**Files:**
- Create: `MeetNow.Recorder/Audio/SessionAudioPlayer.cs`

- [ ] **Step 1: Create SessionAudioPlayer**

Create `MeetNow.Recorder/Audio/SessionAudioPlayer.cs`:

```csharp
using System.IO;
using System.Text.Json;
using NAudio.Wave;

namespace MeetNow.Recorder;

public enum AudioChannel { Loopback, Mic, Both }

public class SessionAudioPlayer : IDisposable
{
    private readonly string _sessionDir;
    private readonly List<ChunkInfo> _chunks = [];
    private WaveOutEvent? _waveOut;
    private WaveStream? _reader;
    private AudioChannel _channel = AudioChannel.Loopback;

    public TimeSpan TotalDuration { get; private set; }
    public TimeSpan Position => _reader?.CurrentTime ?? TimeSpan.Zero;
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public SessionAudioPlayer(string sessionDir)
    {
        _sessionDir = sessionDir;
        LoadChunkInfo();
    }

    private void LoadChunkInfo()
    {
        var chunksDir = Path.Combine(_sessionDir, "chunks");
        if (!Directory.Exists(chunksDir)) return;

        double totalSeconds = 0;
        foreach (var file in Directory.GetFiles(chunksDir, "chunk_*.json")
                     .Where(f => !f.Contains("_loopback") && !f.Contains("_mic"))
                     .OrderBy(f => f))
        {
            try
            {
                var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var index = root.GetProperty("chunkIndex").GetInt32();
                var duration = root.GetProperty("durationSeconds").GetDouble();
                var loopbackFile = Path.Combine(chunksDir, root.GetProperty("loopbackFile").GetString()!);
                var micFile = Path.Combine(chunksDir, root.GetProperty("micFile").GetString()!);

                _chunks.Add(new ChunkInfo(index, loopbackFile, micFile, TimeSpan.FromSeconds(totalSeconds), duration));
                totalSeconds += duration;
            }
            catch { }
        }

        TotalDuration = TimeSpan.FromSeconds(totalSeconds);
    }

    public void Play()
    {
        if (_waveOut == null)
            BuildPipeline();

        _waveOut?.Play();
    }

    public void Pause()
    {
        _waveOut?.Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (_reader == null) return;
        var clamped = TimeSpan.FromSeconds(Math.Clamp(position.TotalSeconds, 0, TotalDuration.TotalSeconds));
        _reader.CurrentTime = clamped;
    }

    public void SetChannel(AudioChannel channel)
    {
        if (_channel == channel) return;
        _channel = channel;
        var wasPlaying = IsPlaying;
        var pos = Position;
        DisposePipeline();
        BuildPipeline();
        Seek(pos);
        if (wasPlaying) Play();
    }

    private void BuildPipeline()
    {
        if (_chunks.Count == 0) return;

        // Concatenate all chunk WAV files for selected channel(s)
        var readers = new List<WaveStream>();

        foreach (var chunk in _chunks)
        {
            switch (_channel)
            {
                case AudioChannel.Loopback:
                    if (File.Exists(chunk.LoopbackFile))
                        readers.Add(new WaveFileReader(chunk.LoopbackFile));
                    break;
                case AudioChannel.Mic:
                    if (File.Exists(chunk.MicFile))
                        readers.Add(new WaveFileReader(chunk.MicFile));
                    break;
                case AudioChannel.Both:
                    // Mix both channels — read loopback as primary, mic for stereo later
                    if (File.Exists(chunk.LoopbackFile))
                        readers.Add(new WaveFileReader(chunk.LoopbackFile));
                    break;
            }
        }

        if (readers.Count == 0) return;

        _reader = new ConcatenatingWaveStream(readers);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_reader);
    }

    private void DisposePipeline()
    {
        _waveOut?.Stop();
        _waveOut?.Dispose();
        _waveOut = null;
        _reader?.Dispose();
        _reader = null;
    }

    public void Dispose()
    {
        DisposePipeline();
    }

    private record ChunkInfo(int Index, string LoopbackFile, string MicFile, TimeSpan StartOffset, double DurationSeconds);
}

/// <summary>Concatenates multiple WaveStreams into one seekable stream.</summary>
internal class ConcatenatingWaveStream : WaveStream
{
    private readonly List<WaveStream> _streams;
    private readonly long _totalLength;
    private int _currentIndex;

    public ConcatenatingWaveStream(List<WaveStream> streams)
    {
        _streams = streams;
        _totalLength = streams.Sum(s => s.Length);
    }

    public override WaveFormat WaveFormat => _streams[0].WaveFormat;
    public override long Length => _totalLength;

    public override long Position
    {
        get
        {
            long pos = 0;
            for (int i = 0; i < _currentIndex && i < _streams.Count; i++)
                pos += _streams[i].Length;
            if (_currentIndex < _streams.Count)
                pos += _streams[_currentIndex].Position;
            return pos;
        }
        set
        {
            long remaining = value;
            for (int i = 0; i < _streams.Count; i++)
            {
                if (remaining < _streams[i].Length)
                {
                    _currentIndex = i;
                    _streams[i].Position = remaining;
                    // Reset subsequent streams
                    for (int j = i + 1; j < _streams.Count; j++)
                        _streams[j].Position = 0;
                    return;
                }
                remaining -= _streams[i].Length;
            }
            // Past end
            _currentIndex = _streams.Count - 1;
            _streams[_currentIndex].Position = _streams[_currentIndex].Length;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (totalRead < count && _currentIndex < _streams.Count)
        {
            int read = _streams[_currentIndex].Read(buffer, offset + totalRead, count - totalRead);
            if (read == 0)
            {
                _currentIndex++;
                continue;
            }
            totalRead += read;
        }
        return totalRead;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var s in _streams) s.Dispose();
        base.Dispose(disposing);
    }
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds. All references from StatusWindow to SessionAudioPlayer/AudioChannel are now resolved.

- [ ] **Step 3: Commit**

```bash
git add MeetNow.Recorder/Audio/
git commit -m "feat(recorder): add SessionAudioPlayer with chunk concatenation

NAudio-based player concatenates chunk WAV files into a seekable
stream. Supports loopback/mic/both channel switching and seek."
```

---

## Task 8: Wire Everything Together and Test

**Files:**
- Modify: `MeetNow.Recorder/App.xaml.cs`
- Modify: `MeetNow.Recorder/TrayIcon.cs`

- [ ] **Step 1: Pass RecorderService to TrayIcon for StatusWindow**

The current `TrayIcon.cs` already receives `RecorderService` and passes it to `StatusWindow`. Verify the constructor wiring in `App.xaml.cs`:

```csharp
// In App.OnStartup (already in Task 1 code):
_trayIcon = new TrayIcon(_service, _cts);
```

This is already correct from Task 1. No changes needed.

- [ ] **Step 2: Build the full app**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Clean build with zero errors.

- [ ] **Step 3: Copy Python transcriber to build output**

```bash
rm -rf MeetNow.Recorder/bin/Debug/net9.0-windows10.0.19041.0/win-x64/MeetNow.Recorder.Transcriber
cp -r MeetNow.Recorder.Transcriber MeetNow.Recorder/bin/Debug/net9.0-windows10.0.19041.0/win-x64/MeetNow.Recorder.Transcriber
rm -rf MeetNow.Recorder/bin/Debug/net9.0-windows10.0.19041.0/win-x64/MeetNow.Recorder.Transcriber/transcriber/__pycache__
```

- [ ] **Step 4: Run the app and verify**

Run: `dotnet run --project MeetNow.Recorder/MeetNow.Recorder.csproj --no-build`

Expected behavior:
1. App starts with no visible window
2. Tray icon appears in system tray
3. Recording starts automatically (check logs in `%TEMP%\MeetNow.Recorder.log`)
4. Right-click tray icon → context menu appears with "Open Status Window" and "Exit"
5. Double-click tray icon → Status Window opens showing session list
6. When audio is detected, tray icon changes to recording indicator
7. Completed sessions show transcript and audio player

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(recorder): wire tray icon, status window, and audio player

Complete WPF tray recorder app with live audio meters, session list,
transcript viewer with click-to-seek, and audio playback."
```

---

## Task 9: Polish and Edge Cases

**Files:**
- Modify: `MeetNow.Recorder/StatusWindow.xaml.cs`
- Modify: `MeetNow.Recorder/TrayIcon.cs`

- [ ] **Step 1: Add transcribing state to tray icon**

In `TrayIcon.cs`, update `OnStateChanged` and add a method to check transcription status. Subscribe to `ChunkFlushed` to update the tooltip with transcription progress:

```csharp
// In TrayIcon constructor, add:
_service.ChunkFlushed += OnChunkFlushed;

// Add method:
private void OnChunkFlushed(int chunkIndex, string sessionId)
{
    Application.Current?.Dispatcher.Invoke(() =>
    {
        if (_service.IsRecording)
            _taskbarIcon.ToolTipText = $"MeetNow Recorder — Recording (chunk {chunkIndex})";
    });
}
```

- [ ] **Step 2: Add window position persistence**

In `StatusWindow.xaml.cs`, save/restore window position using a simple JSON file:

```csharp
// At end of constructor:
LoadWindowPosition();

// Add methods:
private void LoadWindowPosition()
{
    var settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetNow", "recorder_window.json");
    if (!File.Exists(settingsPath)) return;

    try
    {
        var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        var root = doc.RootElement;
        Left = root.GetProperty("left").GetDouble();
        Top = root.GetProperty("top").GetDouble();
        Width = root.GetProperty("width").GetDouble();
        Height = root.GetProperty("height").GetDouble();
    }
    catch { }
}

// In OnClosing, before Hide():
private void SaveWindowPosition()
{
    var settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MeetNow", "recorder_window.json");
    var json = JsonSerializer.Serialize(new { left = Left, top = Top, width = Width, height = Height });
    File.WriteAllText(settingsPath, json);
}
```

Call `SaveWindowPosition()` at the start of `OnClosing`.

- [ ] **Step 3: Add low disk space warning bar**

In `StatusWindow.xaml`, add above the Grid:

```xml
<!-- Warning bar at top of detail pane -->
<Border x:Name="DiskWarningBar" DockPanel.Dock="Top" Background="#FF9800"
        Padding="8,4" Visibility="Collapsed" CornerRadius="4" Margin="0,0,0,8">
    <TextBlock x:Name="DiskWarningText" Foreground="Black" FontSize="11" />
</Border>
```

In the `FreeDiskMb` property change handler:

```csharp
case nameof(RecorderViewModel.FreeDiskMb):
    DiskUsageText.Text = $"Disk: {_viewModel.FreeDiskMb / 1024.0:F1} GB free";
    if (_viewModel.FreeDiskMb < 500)
    {
        DiskWarningBar.Visibility = Visibility.Visible;
        DiskWarningBar.Background = new SolidColorBrush(Color.FromRgb(0xF4, 0x43, 0x36));
        DiskWarningText.Text = $"CRITICAL: Only {_viewModel.FreeDiskMb} MB free!";
    }
    else if (_viewModel.FreeDiskMb < 1000)
    {
        DiskWarningBar.Visibility = Visibility.Visible;
        DiskWarningText.Text = $"Low disk space: {_viewModel.FreeDiskMb} MB free";
    }
    else
    {
        DiskWarningBar.Visibility = Visibility.Collapsed;
    }
    break;
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build MeetNow.Recorder/MeetNow.Recorder.csproj`

Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(recorder): add polish — tooltip progress, window persistence, disk warnings

Tray tooltip shows chunk count during recording. Window position
saved between opens. Low/critical disk warnings in status window."
```
