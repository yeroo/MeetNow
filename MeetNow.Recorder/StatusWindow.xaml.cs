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

        _viewModel = new RecorderViewModel(service);
        SessionList.ItemsSource = _viewModel.Sessions;

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        // Select first session if available
        if (_viewModel.Sessions.Count > 0)
            SessionList.SelectedIndex = 0;

        UpdateDiskUsage();
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
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
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

        // Recording dot
        RecordingDot.Visibility = session.Status == "recording"
            ? Visibility.Visible : Visibility.Collapsed;

        // Audio meters: visible only for active recording session
        AudioMetersPanel.Visibility = session.Status == "recording"
            ? Visibility.Visible : Visibility.Collapsed;

        // Action buttons: visible for completed/failed sessions
        ActionButtonsPanel.Visibility = session.Status != "recording"
            ? Visibility.Visible : Visibility.Collapsed;

        // Transcript button enabled only if transcript exists
        OpenTranscriptBtn.IsEnabled = session.HasTranscript;

        // Retry button enabled only if there are failed chunks
        RetryTranscriptionBtn.IsEnabled = session.FailedChunks > 0;

        // Load transcript
        LoadTranscript(session);

        // Audio player: available for completed sessions with chunks
        if (session.Status != "recording" && session.TotalChunks > 0)
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
                    continue;

                var tb = new TextBlock
                {
                    Text = line,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 2),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 12,
                    Cursor = Cursors.Hand
                };

                // Parse timestamp and speaker from line format: "[HH:MM:SS] Speaker: text"
                var (timestamp, speaker) = ParseTranscriptLine(line);
                tb.Tag = timestamp;
                tb.MouseLeftButtonDown += TranscriptLine_Click;

                if (line.StartsWith("---"))
                {
                    tb.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(SeparatorColor));
                    tb.Cursor = Cursors.Arrow;
                    tb.MouseLeftButtonDown -= TranscriptLine_Click;
                }
                else if (speaker != null)
                {
                    var color = SpeakerColors.TryGetValue(speaker, out var c) ? c : DefaultSpeakerColor;
                    tb.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(color));
                }
                else
                {
                    tb.Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString("#E0E0E0"));
                }

                TranscriptPanel.Children.Add(tb);
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

                var statusColor = status switch
                {
                    "transcribed" => "#4CAF50",
                    "transcribing" => "#FF9800",
                    "failed" => "#F44336",
                    _ => "#888888"
                };

                var header = new TextBlock
                {
                    Text = $"Chunk {chunkIndex} - {duration:F1}s - {status}",
                    Foreground = new SolidColorBrush(
                        (Color)ColorConverter.ConvertFromString(statusColor)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 6, 0, 2)
                };
                TranscriptPanel.Children.Add(header);

                // For transcribed chunks, show inline segments from transcript JSON
                if (status == "transcribed")
                {
                    LoadChunkTranscriptSegments(session, chunkIndex);
                }
            }
            catch
            {
                // Skip malformed chunk metadata
            }
        }
    }

    private void LoadChunkTranscriptSegments(SessionViewModel session, int chunkIndex)
    {
        var indexStr = chunkIndex.ToString("D3");
        var transcriptPath = Path.Combine(session.SessionDir, "transcripts",
            $"chunk_{indexStr}_loopback.json");

        if (!File.Exists(transcriptPath))
            return;

        try
        {
            var json = File.ReadAllText(transcriptPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("segments", out var segmentsArr))
            {
                foreach (var seg in segmentsArr.EnumerateArray())
                {
                    var text = seg.TryGetProperty("text", out var textProp)
                        ? textProp.GetString()?.Trim() ?? ""
                        : "";

                    if (string.IsNullOrWhiteSpace(text))
                        continue;

                    var start = seg.TryGetProperty("start", out var startProp)
                        ? startProp.GetDouble() : 0;

                    var tb = new TextBlock
                    {
                        Text = $"  {text}",
                        TextWrapping = TextWrapping.Wrap,
                        Foreground = new SolidColorBrush(
                            (Color)ColorConverter.ConvertFromString("#B0B0B0")),
                        FontFamily = new FontFamily("Segoe UI"),
                        FontSize = 11,
                        Margin = new Thickness(12, 1, 0, 1),
                        Tag = TimeSpan.FromSeconds(start),
                        Cursor = Cursors.Hand
                    };
                    tb.MouseLeftButtonDown += TranscriptLine_Click;
                    TranscriptPanel.Children.Add(tb);
                }
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

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (_player == null) return;

        if (_player.IsPlaying)
        {
            _player.Pause();
            StopPositionTimer();
        }
        else
        {
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
        TextBlock? closest = null;
        double closestDiff = double.MaxValue;

        foreach (var child in TranscriptPanel.Children)
        {
            if (child is TextBlock tb && tb.Tag is TimeSpan ts)
            {
                tb.FontWeight = FontWeights.Normal;
                tb.Opacity = 0.7;

                var diff = Math.Abs((currentPos - ts).TotalSeconds);
                if (diff < closestDiff)
                {
                    closestDiff = diff;
                    closest = tb;
                }
            }
        }

        if (closest != null)
        {
            closest.FontWeight = FontWeights.SemiBold;
            closest.Opacity = 1.0;
        }
    }

    private void StopAudio()
    {
        StopPositionTimer();
        _player?.Dispose();
        _player = null;
    }

    // --- Action Buttons ---

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
