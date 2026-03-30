using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MeetNow.Recorder.Audio;
using MeetNow.Recorder.ViewModels;
using MeetNow.Recording.Core.Recording;

namespace MeetNow.Recorder;

public partial class StatusWindow : Window
{
    private readonly RecorderViewModel _viewModel;
    private readonly RecorderService _service;
    private SessionAudioPlayer? _player;
    private DispatcherTimer? _positionTimer;
    private bool _isSeeking;

    // Speaker colors for transcript display
    private static readonly Dictionary<string, string> SpeakerColors = new()
    {
        ["Me"] = "#81C784",
        ["Person 1"] = "#64B5F6",
        ["Person 2"] = "#CE93D8",
        ["Person 3"] = "#FFB74D",
    };
    private const string DefaultSpeakerColor = "#80DEEA";
    private const string SeparatorColor = "#333333";

    public StatusWindow(RecorderService service)
    {
        InitializeComponent();

        _service = service;
        _viewModel = new RecorderViewModel(service);
        SessionList.ItemsSource = _viewModel.Sessions;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Select first session if available
        if (_viewModel.Sessions.Count > 0)
            SessionList.SelectedIndex = 0;

        UpdateDiskUsage();
        LoadWindowPosition();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
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
                UpdateRecordingState();
                break;
            case nameof(RecorderViewModel.FreeDiskMb):
                UpdateDiskUsage();
                break;
        }
    }

    private void UpdateRecordingState()
    {
        var isRecording = _viewModel.State is RecorderState.Recording
            or RecorderState.MicKeepalive or RecorderState.Draining;

        // Show audio meters when actively recording
        AudioMetersPanel.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;

        // Update the currently selected session if it's the active recording
        if (SessionList.SelectedItem is SessionViewModel session && session.Status == "recording")
        {
            RecordingDot.Visibility = isRecording ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void UpdateDiskUsage()
    {
        DiskUsageText.Text = $"Disk: {_viewModel.FreeDiskMb:N0} MB free";
        UpdateDiskWarningBar(_viewModel.FreeDiskMb);
    }

    private void UpdateDiskWarningBar(long freeMb)
    {
        if (freeMb < 500)
        {
            DiskWarningBar.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#F44336"));
            DiskWarningText.Text = $"CRITICAL: Only {freeMb} MB free!";
            DiskWarningBar.Visibility = Visibility.Visible;
        }
        else if (freeMb < 1000)
        {
            DiskWarningBar.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#FF9800"));
            DiskWarningText.Text = $"Low disk space: {freeMb} MB free";
            DiskWarningBar.Visibility = Visibility.Visible;
        }
        else
        {
            DiskWarningBar.Visibility = Visibility.Collapsed;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        SaveWindowPosition();
        e.Cancel = true;
        Hide();
    }

    private void LoadWindowPosition()
    {
        var settingsPath = System.IO.Path.Combine(
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

    private void SaveWindowPosition()
    {
        var settingsPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "recorder_window.json");
        try
        {
            var json = JsonSerializer.Serialize(new { left = Left, top = Top, width = Width, height = Height });
            File.WriteAllText(settingsPath, json);
        }
        catch { }
    }

    private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SessionList.SelectedItem is SessionViewModel session)
            ShowSessionDetail(session);
    }

    private void ShowSessionDetail(SessionViewModel session)
    {
        // Stop any playing audio
        StopAudio();

        // Set header
        var localTime = session.StartTimeUtc.ToLocalTime();
        HeaderText.Text = localTime.ToString("ddd, MMM d yyyy  h:mm tt", CultureInfo.InvariantCulture);

        var durationStr = session.Duration.TotalMinutes >= 1
            ? $"{session.Duration:h\\:mm\\:ss}"
            : $"{session.Duration.TotalSeconds:F0}s";
        SubHeaderText.Text = $"{session.Status} - {durationStr} - {session.TotalChunks} chunks";

        // Determine if this is the currently-active recording session
        bool isActiveRecording = session.Status == "recording"
            && session.SessionId == _viewModel.ActiveSessionId;

        bool isStale = session.Status == "recording" && !isActiveRecording
            && session.IsStaleRecording();

        // Recording dot: show for active recordings only
        RecordingDot.Visibility = isActiveRecording
            ? Visibility.Visible : Visibility.Collapsed;

        // Audio meters: visible only for the active recording session
        AudioMetersPanel.Visibility = isActiveRecording
            ? Visibility.Visible : Visibility.Collapsed;

        // Action buttons: visible for ALL sessions except the currently-active recording
        ActionButtonsPanel.Visibility = !isActiveRecording
            ? Visibility.Visible : Visibility.Collapsed;

        // "Complete Session" button: visible when session is stale recording
        CompleteSessionBtn.Visibility = isStale
            ? Visibility.Visible : Visibility.Collapsed;

        // "Generate Transcript" button: visible when transcribed chunks exist but no transcript.txt
        bool hasTranscribedChunks = session.TranscribedChunks > 0;
        GenerateTranscriptBtn.Visibility = hasTranscribedChunks && !session.HasTranscript
            ? Visibility.Visible : Visibility.Collapsed;

        // Transcript button enabled only if transcript exists
        OpenTranscriptBtn.IsEnabled = session.HasTranscript;

        // Retry button enabled only if there are failed chunks
        RetryTranscriptionBtn.IsEnabled = session.FailedChunks > 0;

        // Load transcript
        LoadTranscript(session);

        // Audio player: available for non-active sessions with chunks (including stale recordings)
        if (!isActiveRecording && session.TotalChunks > 0)
        {
            SetupAudioPlayer(session);
            AudioPlayerBar.Visibility = Visibility.Visible;
        }
        else
        {
            AudioPlayerBar.Visibility = Visibility.Collapsed;
        }
    }

    private void LoadTranscript(SessionViewModel session)
    {
        TranscriptPanel.Children.Clear();

        if (session.HasTranscript)
        {
            LoadTranscriptFile(session);
        }
        else
        {
            LoadChunkPlaceholders(session);
        }
    }

    private void LoadTranscriptFile(SessionViewModel session)
    {
        try
        {
            var lines = File.ReadAllLines(session.TranscriptPath);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    TranscriptPanel.Children.Add(new Border { Height = 6 });
                    continue;
                }

                var (timestamp, speaker) = ParseTranscriptLine(line);

                var colorHex = line.StartsWith("=") || line.StartsWith("Meeting") || line.StartsWith("Duration")
                    ? "#888888"
                    : speaker != null
                        ? SpeakerColors.TryGetValue(speaker, out var c) ? c : DefaultSpeakerColor
                        : "#E0E0E0";
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));

                // Row: [play button gutter] [selectable text]
                var row = new Grid { Tag = timestamp };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                // Play button in gutter (green triangle, like VS breakpoint area)
                if (timestamp.HasValue)
                {
                    var playBtn = new Button
                    {
                        Content = "\u25B6", // ▶
                        FontSize = 8,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0),
                        Cursor = Cursors.Hand,
                        VerticalAlignment = VerticalAlignment.Top,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        Margin = new Thickness(0, 3, 0, 0),
                        Tag = timestamp,
                        ToolTip = "Play from here",
                    };
                    playBtn.Click += PlayFromTimestamp_Click;
                    Grid.SetColumn(playBtn, 0);
                    row.Children.Add(playBtn);
                }

                // Selectable, copyable text
                var textBox = new TextBox
                {
                    Text = line,
                    IsReadOnly = true,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Foreground = brush,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Padding = new Thickness(0, 1, 0, 1),
                    Margin = new Thickness(0),
                    IsTabStop = false,
                };
                Grid.SetColumn(textBox, 1);
                row.Children.Add(textBox);

                TranscriptPanel.Children.Add(row);
            }
        }
        catch (Exception ex)
        {
            TranscriptPanel.Children.Add(new TextBlock
            {
                Text = $"Error loading transcript: {ex.Message}",
                Foreground = Brushes.Red,
                FontFamily = new FontFamily("Segoe UI")
            });
        }
    }

    private void PlayFromTimestamp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TimeSpan ts } && _player != null)
        {
            // Auto-pause recording
            if (!_service.Paused)
            {
                _service.Paused = true;
                _pausedByPlayback = true;
            }

            _player.Seek(ts);
            if (!_player.IsPlaying)
            {
                _player.Play();
                StartPositionTimer();
                UpdatePlayPauseButton();
            }
            HighlightCurrentSegment();
        }
    }

    private static (TimeSpan? timestamp, string? speaker) ParseTranscriptLine(string line)
    {
        // Expected format: "[HH:MM:SS] Speaker: text" or "[MM:SS] Speaker: text"
        TimeSpan? timestamp = null;
        string? speaker = null;

        if (line.Length > 1 && line[0] == '[')
        {
            int closeBracket = line.IndexOf(']');
            if (closeBracket > 0)
            {
                var timeStr = line[1..closeBracket];
                if (TimeSpan.TryParse(timeStr, out var ts))
                    timestamp = ts;
                else if (TryParseMinSec(timeStr, out ts))
                    timestamp = ts;

                // Parse speaker after "] "
                var rest = line[(closeBracket + 1)..].TrimStart();
                int colonIdx = rest.IndexOf(':');
                if (colonIdx > 0)
                    speaker = rest[..colonIdx].Trim();
            }
        }

        return (timestamp, speaker);
    }

    private static bool TryParseMinSec(string s, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var parts = s.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out int min)
            && int.TryParse(parts[1], out int sec))
        {
            result = new TimeSpan(0, min, sec);
            return true;
        }
        return false;
    }

    private void LoadChunkPlaceholders(SessionViewModel session)
    {
        var chunksDir = Path.Combine(session.SessionDir, "chunks");
        if (!Directory.Exists(chunksDir))
        {
            TranscriptPanel.Children.Add(new TextBlock
            {
                Text = "No chunks recorded yet.",
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#888888")),
                FontFamily = new FontFamily("Segoe UI")
            });
            return;
        }

        var chunkFiles = Directory.GetFiles(chunksDir, "chunk_*.json")
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return !name.Contains("_loopback") && !name.Contains("_mic");
            })
            .OrderBy(f => f)
            .ToList();

        // Collect all interleaved segments across all transcribed chunks
        var allSegments = new List<(DateTime absoluteUtc, double relativeStart, string speaker, string text)>();
        // Track non-transcribed chunks for status display
        var nonTranscribedChunks = new List<(int index, double duration, string status)>();

        foreach (var chunkFile in chunkFiles)
        {
            try
            {
                var json = File.ReadAllText(chunkFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var chunkIndex = root.GetProperty("chunkIndex").GetInt32();
                var duration = root.GetProperty("durationSeconds").GetDouble();
                var status = root.TryGetProperty("status", out var statusProp)
                    ? statusProp.GetString() ?? "unknown"
                    : "unknown";

                if (status == "transcribed")
                {
                    var chunkStartUtc = root.TryGetProperty("startTimeUtc", out var sp) && sp.TryGetDateTime(out var dt)
                        ? dt
                        : session.StartTimeUtc;

                    // Load loopback segments
                    LoadSegmentsFromJson(session, chunkIndex, "_loopback", chunkStartUtc, "Other", allSegments);
                    // Load mic segments
                    LoadSegmentsFromJson(session, chunkIndex, "_mic", chunkStartUtc, "Me", allSegments);
                }
                else
                {
                    nonTranscribedChunks.Add((chunkIndex, duration, status));
                }
            }
            catch
            {
                // Skip malformed chunk metadata
            }
        }

        // Show non-transcribed chunk headers
        foreach (var (index, duration, status) in nonTranscribedChunks)
        {
            var statusColor = status switch
            {
                "transcribing" => "#FF9800",
                "failed" => "#F44336",
                _ => "#888888"
            };

            var header = new TextBlock
            {
                Text = $"Chunk {index} - {duration:F1}s - {status}",
                Foreground = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(statusColor)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 2)
            };
            TranscriptPanel.Children.Add(header);
        }

        // Sort interleaved segments by absolute time and display
        if (allSegments.Count > 0)
        {
            allSegments.Sort((a, b) => a.absoluteUtc.CompareTo(b.absoluteUtc));

            string? lastSpeaker = null;
            foreach (var (absoluteUtc, relativeStart, speaker, text) in allSegments)
            {
                // Blank line between speaker changes
                if (lastSpeaker != null && speaker != lastSpeaker)
                {
                    TranscriptPanel.Children.Add(new TextBlock
                    {
                        Text = "",
                        FontSize = 4,
                        Margin = new Thickness(0, 2, 0, 2)
                    });
                }

                var localTime = absoluteUtc.ToLocalTime();
                var timeStr = localTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                var color = SpeakerColors.TryGetValue(speaker, out var c) ? c : DefaultSpeakerColor;

                var tb = new TextBlock
                {
                    Text = $"[{timeStr}] {speaker}: {text}",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(color)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    Margin = new Thickness(0, 1, 0, 1),
                    Tag = TimeSpan.FromSeconds(relativeStart),
                    Cursor = Cursors.Hand
                };
                tb.MouseLeftButtonDown += TranscriptLine_Click;
                TranscriptPanel.Children.Add(tb);

                lastSpeaker = speaker;
            }
        }
    }

    private static void LoadSegmentsFromJson(
        SessionViewModel session, int chunkIndex, string channelSuffix,
        DateTime chunkStartUtc, string speaker,
        List<(DateTime absoluteUtc, double relativeStart, string speaker, string text)> segments)
    {
        var indexStr = chunkIndex.ToString("D3");
        var transcriptPath = Path.Combine(session.SessionDir, "transcripts",
            $"chunk_{indexStr}{channelSuffix}.json");

        if (!File.Exists(transcriptPath))
            return;

        try
        {
            var json = File.ReadAllText(transcriptPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("segments", out var segmentsArr))
                return;

            foreach (var seg in segmentsArr.EnumerateArray())
            {
                var text = seg.TryGetProperty("text", out var textProp)
                    ? textProp.GetString()?.Trim() ?? ""
                    : "";

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var start = seg.TryGetProperty("start", out var startProp)
                    ? startProp.GetDouble() : 0;

                var absoluteUtc = chunkStartUtc.AddSeconds(start);
                segments.Add((absoluteUtc, start, speaker, text));
            }
        }
        catch
        {
            // Skip unreadable transcript JSON
        }
    }

    private void TranscriptLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is TimeSpan ts && _player != null)
        {
            _player.Seek(ts);
            if (!_player.IsPlaying)
                _player.Play();
            UpdatePlayPauseButton();
            StartPositionTimer();
        }
    }

    // --- Audio Player Controls ---

    private void SetupAudioPlayer(SessionViewModel session)
    {
        _player?.Dispose();
        _player = new SessionAudioPlayer(session.SessionDir);

        SeekSlider.Maximum = _player.TotalDuration.TotalSeconds;
        SeekSlider.Value = 0;
        UpdatePositionText();

        // Reset channel selection
        ChannelLoopback.IsChecked = true;
    }

    private bool _pausedByPlayback;

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        if (_player.IsPlaying)
        {
            _player.Pause();
            StopPositionTimer();

            // Resume recording if we paused it
            if (_pausedByPlayback)
            {
                _service.Paused = false;
                _pausedByPlayback = false;
            }
        }
        else
        {
            // Auto-pause recording while playing back
            if (!_service.Paused)
            {
                _service.Paused = true;
                _pausedByPlayback = true;
            }

            _player.Play();
            StartPositionTimer();
        }

        UpdatePlayPauseButton();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isSeeking || _player == null) return;

        // Only seek if the user is dragging (mouse is captured)
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            _player.Seek(TimeSpan.FromSeconds(e.NewValue));
            UpdatePositionText();
            HighlightCurrentSegment();
        }
    }

    private void Channel_Changed(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        AudioChannel channel = AudioChannel.Loopback;
        if (ChannelMic.IsChecked == true) channel = AudioChannel.Mic;
        else if (ChannelBoth.IsChecked == true) channel = AudioChannel.Both;

        _player.SetChannel(channel);
    }

    private void StartPositionTimer()
    {
        if (_positionTimer != null) return;

        _positionTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _positionTimer.Tick += PositionTimer_Tick;
        _positionTimer.Start();
    }

    private void StopPositionTimer()
    {
        _positionTimer?.Stop();
        _positionTimer = null;
    }

    private void PositionTimer_Tick(object? sender, EventArgs e)
    {
        if (_player == null || !_player.IsPlaying)
        {
            StopPositionTimer();
            UpdatePlayPauseButton();
            return;
        }

        _isSeeking = true;
        SeekSlider.Value = _player.Position.TotalSeconds;
        _isSeeking = false;

        UpdatePositionText();
        HighlightCurrentSegment();
    }

    private void UpdatePlayPauseButton()
    {
        PlayPauseBtn.Content = _player?.IsPlaying == true ? "\u23F8" : "\u25B6";
    }

    private void UpdatePositionText()
    {
        if (_player == null) return;

        var pos = _player.Position;
        var total = _player.TotalDuration;
        PositionText.Text = $"{FormatTime(pos)} / {FormatTime(total)}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"m\:ss");
    }

    private void HighlightCurrentSegment()
    {
        if (_player == null) return;

        var currentPos = _player.Position;
        Grid? closestRow = null;
        double closestDiff = double.MaxValue;

        foreach (var child in TranscriptPanel.Children)
        {
            if (child is Grid row && row.Tag is TimeSpan ts)
            {
                row.Background = Brushes.Transparent;

                var diff = Math.Abs((currentPos - ts).TotalSeconds);
                if (diff < closestDiff)
                {
                    closestDiff = diff;
                    closestRow = row;
                }
            }
        }

        if (closestRow != null)
        {
            closestRow.Background = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF));
            closestRow.BringIntoView();
        }
    }

    private void StopAudio()
    {
        StopPositionTimer();
        _player?.Dispose();
        _player = null;

        // Resume recording if we paused it for playback
        if (_pausedByPlayback)
        {
            _service.Paused = false;
            _pausedByPlayback = false;
        }
    }

    // --- Action Buttons ---

    private void CompleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        session.ForceComplete();
        ShowSessionDetail(session);
    }

    private void GenerateTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        // Force-complete first if still stuck in "recording" status
        if (session.Status == "recording")
        {
            session.ForceComplete();
        }

        try
        {
            TranscriptGenerator.Generate(session.SessionDir);
            session.Refresh();
            ShowSessionDetail(session);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to generate transcript: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenTranscript_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        if (File.Exists(session.TranscriptPath))
        {
            Process.Start(new ProcessStartInfo(session.TranscriptPath)
            {
                UseShellExecute = true
            });
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        if (Directory.Exists(session.SessionDir))
        {
            Process.Start(new ProcessStartInfo(session.SessionDir)
            {
                UseShellExecute = true
            });
        }
    }

    private void RetryTranscription_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        var chunksDir = Path.Combine(session.SessionDir, "chunks");
        if (!Directory.Exists(chunksDir))
            return;

        int resetCount = 0;

        foreach (var file in Directory.GetFiles(chunksDir, "chunk_*.json"))
        {
            var fileName = Path.GetFileNameWithoutExtension(file);
            if (fileName.Contains("_loopback") || fileName.Contains("_mic"))
                continue;

            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("status", out var statusProp))
                {
                    var status = statusProp.GetString();
                    if (status is "failed" or "transcribing")
                    {
                        // Rewrite with pending_transcription status
                        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
                        dict["status"] = JsonSerializer.SerializeToElement("pending_transcription");

                        // Remove error field if present
                        dict.Remove("error");
                        dict.Remove("claimedAtUtc");

                        var updated = JsonSerializer.Serialize(dict, new JsonSerializerOptions
                        {
                            WriteIndented = true
                        });
                        File.WriteAllText(file, updated);
                        resetCount++;
                    }
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        if (resetCount > 0)
        {
            session.Refresh();
            ShowSessionDetail(session);
        }

        MessageBox.Show($"Reset {resetCount} chunk(s) to pending.", "Retry Transcription",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void DeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not SessionViewModel session)
            return;

        var result = MessageBox.Show(
            $"Delete session {session.SessionId} and all its recordings?",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        StopAudio();

        try
        {
            if (Directory.Exists(session.SessionDir))
                Directory.Delete(session.SessionDir, recursive: true);

            _viewModel.Sessions.Remove(session);

            // Clear detail pane
            HeaderText.Text = "Select a session";
            SubHeaderText.Text = "";
            TranscriptPanel.Children.Clear();
            ActionButtonsPanel.Visibility = Visibility.Collapsed;
            AudioPlayerBar.Visibility = Visibility.Collapsed;
            RecordingDot.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to delete session: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
