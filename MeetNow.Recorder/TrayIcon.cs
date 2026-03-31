using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using MeetNow.Recording.Core.Recording;
using Serilog;

namespace MeetNow.Recorder;

public class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _taskbarIcon;
    private readonly RecorderService _service;
    private readonly Icon _iconIdle;
    private readonly Icon _iconRecording;
    private readonly Icon _iconTranscribing;
    private StatusWindow? _statusWindow;
    private bool _disposed;

    public TrayIcon(RecorderService service)
    {
        _service = service;

        // Load icons from disk (alongside the executable)
        var baseDir = AppContext.BaseDirectory;
        _iconIdle = new Icon(Path.Combine(baseDir, "Icons", "icon-idle.ico"));
        _iconRecording = new Icon(Path.Combine(baseDir, "Icons", "icon-recording.ico"));
        _iconTranscribing = new Icon(Path.Combine(baseDir, "Icons", "icon-transcribing.ico"));

        _taskbarIcon = new TaskbarIcon
        {
            Icon = _iconIdle,
            ToolTipText = "MeetNow Recorder — Idle",
            ContextMenu = BuildContextMenu()
        };

        _taskbarIcon.TrayMouseDoubleClick += OnDoubleClick;

        // Subscribe to state changes to swap icons
        _service.StateChanged += OnStateChanged;
        _service.ChunkFlushed += OnChunkFlushed;

        Log.Information("Tray icon initialized");
    }

    private MenuItem? _pauseItem;

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open Status Window", FontWeight = FontWeights.Bold };
        openItem.Click += (_, _) => ShowStatusWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(new Separator());

        _pauseItem = new MenuItem { Header = "Pause Recording" };
        _pauseItem.Click += OnPauseClick;
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new Separator());

        var exitItem = new MenuItem { Header = "Exit" };
        exitItem.Click += OnExitClick;
        menu.Items.Add(exitItem);

        return menu;
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        _service.Paused = !_service.Paused;
        _pauseItem!.Header = _service.Paused ? "Resume Recording" : "Pause Recording";
        _taskbarIcon.ToolTipText = _service.Paused
            ? "MeetNow Recorder — Paused"
            : "MeetNow Recorder — Idle";
        _taskbarIcon.Icon = _service.Paused ? _iconTranscribing : _iconIdle;
        Log.Information("Recording {State}", _service.Paused ? "paused" : "resumed");
    }

    private void OnDoubleClick(object sender, RoutedEventArgs e)
    {
        ShowStatusWindow();
    }

    private void ShowStatusWindow()
    {
        try
        {
            if (_statusWindow is { IsLoaded: true })
            {
                _statusWindow.Activate();
                return;
            }

            _statusWindow = new StatusWindow(_service);
            _statusWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to open StatusWindow");
            MessageBox.Show($"Failed to open status window:\n{ex.Message}",
                "MeetNow Recorder", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (_service.IsRecording)
        {
            var result = MessageBox.Show(
                "A recording is in progress. Are you sure you want to exit?",
                "MeetNow Recorder",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;
        }

        Application.Current.Shutdown();
    }

    private void OnStateChanged(RecorderState state)
    {
        // Must update icon on UI thread
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (_service.Paused) return; // Don't update icons while paused

            switch (state)
            {
                case RecorderState.Recording:
                case RecorderState.MicKeepalive:
                case RecorderState.Draining:
                    _taskbarIcon.Icon = _iconRecording;
                    _taskbarIcon.ToolTipText = "MeetNow Recorder — Recording";
                    break;

                case RecorderState.Flushing:
                    _taskbarIcon.Icon = _iconTranscribing;
                    _taskbarIcon.ToolTipText = "MeetNow Recorder — Flushing";
                    break;

                default:
                    _taskbarIcon.Icon = _iconIdle;
                    _taskbarIcon.ToolTipText = "MeetNow Recorder — Idle";
                    break;
            }
        });
    }

    private void OnChunkFlushed(int chunkIndex, string sessionId)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_service.IsRecording)
                _taskbarIcon.ToolTipText = $"MeetNow Recorder — Recording (chunk {chunkIndex})";
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _service.StateChanged -= OnStateChanged;
        _service.ChunkFlushed -= OnChunkFlushed;
        _taskbarIcon.Dispose();
        _iconIdle.Dispose();
        _iconRecording.Dispose();
        _iconTranscribing.Dispose();
    }
}
