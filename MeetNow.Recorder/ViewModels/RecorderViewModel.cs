using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MeetNow.Recording.Core.Recording;
using Serilog;

namespace MeetNow.Recorder.ViewModels;

public class RecorderViewModel : BaseViewModel, IDisposable
{
    private readonly RecorderService _service;
    private readonly Dispatcher _dispatcher;
    private readonly string _recordingsDir;
    private readonly FileSystemWatcher _watcher;

    public ObservableCollection<SessionViewModel> Sessions { get; } = [];

    private SessionViewModel? _selectedSession;
    public SessionViewModel? SelectedSession
    {
        get => _selectedSession;
        set => SetField(ref _selectedSession, value);
    }

    private RecorderState _state;
    public RecorderState State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    private float _loopbackLevel;
    public float LoopbackLevel
    {
        get => _loopbackLevel;
        set => SetField(ref _loopbackLevel, value);
    }

    private float _micLevel;
    public float MicLevel
    {
        get => _micLevel;
        set => SetField(ref _micLevel, value);
    }

    private long _freeDiskMb;
    public long FreeDiskMb
    {
        get => _freeDiskMb;
        set => SetField(ref _freeDiskMb, value);
    }

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

        // Load existing sessions from disk
        LoadExistingSessions();

        // Watch for file changes in recordings directory
        Directory.CreateDirectory(_recordingsDir);
        _watcher = new FileSystemWatcher(_recordingsDir)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
            Filter = "*.json",
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnFileChanged;
        _watcher.Created += OnFileChanged;

        UpdateDiskSpace();
    }

    private void LoadExistingSessions()
    {
        if (!Directory.Exists(_recordingsDir))
            return;

        var sessionDirs = Directory.GetDirectories(_recordingsDir)
            .Where(d => File.Exists(Path.Combine(d, "session.json")))
            .OrderByDescending(d => d)
            .ToList();

        foreach (var dir in sessionDirs)
        {
            try
            {
                Sessions.Add(new SessionViewModel(dir));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load session from {Dir}", dir);
            }
        }

        SelectedSession = Sessions.FirstOrDefault();
    }

    private void OnStateChanged(RecorderState state)
    {
        _dispatcher.InvokeAsync(() => State = state);
    }

    private void OnAudioLevelChanged(float loopback, float mic)
    {
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
            var sessionDir = Path.Combine(_recordingsDir, sessionId);
            if (!Directory.Exists(sessionDir))
                return;

            var vm = new SessionViewModel(sessionDir);
            Sessions.Insert(0, vm);
            SelectedSession = vm;
        });
    }

    private void OnSessionCompleted(string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var session = FindSession(sessionId);
            session?.Refresh();
        });
    }

    private void OnChunkFlushed(int chunkIndex, string sessionId)
    {
        _dispatcher.InvokeAsync(() =>
        {
            var session = FindSession(sessionId);
            session?.Refresh();
        });
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                // Determine which session directory this file belongs to
                var relativePath = Path.GetRelativePath(_recordingsDir, e.FullPath);
                var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (parts.Length < 2)
                    return;

                var sessionId = parts[0];
                var session = FindSession(sessionId);
                session?.Refresh();

                // Update disk space when files change
                UpdateDiskSpace();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Error handling file change: {Path}", e.FullPath);
            }
        });
    }

    private SessionViewModel? FindSession(string sessionId)
    {
        return Sessions.FirstOrDefault(s => s.SessionId == sessionId);
    }

    private void UpdateDiskSpace()
    {
        try
        {
            var root = Path.GetPathRoot(_recordingsDir) ?? "C:\\";
            var drive = new DriveInfo(root);
            FreeDiskMb = drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to read disk space");
        }
    }

    public void Dispose()
    {
        _service.StateChanged -= OnStateChanged;
        _service.AudioLevelChanged -= OnAudioLevelChanged;
        _service.SessionStarted -= OnSessionStarted;
        _service.SessionCompleted -= OnSessionCompleted;
        _service.ChunkFlushed -= OnChunkFlushed;

        _watcher.Changed -= OnFileChanged;
        _watcher.Created -= OnFileChanged;
        _watcher.Dispose();
    }
}
