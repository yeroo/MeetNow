using System.Threading;
using System.Windows;
using MeetNow.Recording.Core.Config;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace MeetNow.Recorder;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private RecorderService? _service;
    private TrayIcon? _trayIcon;
    private CancellationTokenSource? _cts;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Single-instance check
        _singleInstanceMutex = new Mutex(true, "MeetNowRecorder_SingleInstance_A1B2C3", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("MeetNow Recorder is already running.", "MeetNow Recorder",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // Initialize Serilog (file only — no console since WinExe)
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeetNow.Recorder.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        try
        {
            Log.Information("MeetNow Recorder starting...");

            var config = LoadConfig();

            // Auto-generate transcript.txt for any fully-transcribed sessions
            GeneratePendingTranscripts(config.OutputDir);

            _service = new RecorderService(config);
            _trayIcon = new TrayIcon(_service);
            _cts = new CancellationTokenSource();

            // Run service on background thread
            _ = Task.Run(async () =>
            {
                try
                {
                    await _service.RunAsync(_cts.Token);
                }
                catch (Exception ex)
                {
                    Log.Fatal(ex, "Recorder crashed");
                    Dispatcher.Invoke(() => Shutdown());
                }
            });
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Recorder failed to start");
            MessageBox.Show($"Failed to start: {ex.Message}", "MeetNow Recorder",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts?.Cancel();
        _trayIcon?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// On startup: complete any stale "recording" sessions left behind by previous crashes,
    /// then generate transcript.txt for any fully-transcribed sessions missing it.
    /// </summary>
    private static void GeneratePendingTranscripts(string recordingsDir)
    {
        if (!System.IO.Directory.Exists(recordingsDir)) return;

        foreach (var sessionDir in System.IO.Directory.GetDirectories(recordingsDir))
        {
            var sessionJsonPath = System.IO.Path.Combine(sessionDir, "session.json");
            if (!System.IO.File.Exists(sessionJsonPath)) continue;

            // Step 1: Complete stale "recording" sessions.
            // On startup no session is active yet, so ALL "recording" sessions are stale.
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(
                    System.IO.File.ReadAllText(sessionJsonPath));
                var status = doc.RootElement.GetProperty("status").GetString();

                if (status == "recording")
                {
                    // Find endTimeUtc from last chunk
                    DateTime? lastEnd = null;
                    var chunksDir = System.IO.Path.Combine(sessionDir, "chunks");
                    if (System.IO.Directory.Exists(chunksDir))
                    {
                        foreach (var cf in System.IO.Directory.GetFiles(chunksDir, "chunk_*.json"))
                        {
                            var n = System.IO.Path.GetFileNameWithoutExtension(cf);
                            if (n.Contains("_loopback") || n.Contains("_mic")) continue;
                            try
                            {
                                using var cd = System.Text.Json.JsonDocument.Parse(
                                    System.IO.File.ReadAllText(cf));
                                if (cd.RootElement.TryGetProperty("endTimeUtc", out var ep)
                                    && ep.TryGetDateTime(out var et))
                                {
                                    if (lastEnd == null || et > lastEnd) lastEnd = et;
                                }
                            }
                            catch { }
                        }
                    }

                    // Rewrite session.json with completed status
                    var dict = System.Text.Json.JsonSerializer.Deserialize<
                        Dictionary<string, System.Text.Json.JsonElement>>(
                        System.IO.File.ReadAllText(sessionJsonPath))!;
                    dict["status"] = System.Text.Json.JsonSerializer.SerializeToElement("completed");
                    if (lastEnd.HasValue)
                        dict["endTimeUtc"] = System.Text.Json.JsonSerializer.SerializeToElement(lastEnd.Value);
                    System.IO.File.WriteAllText(sessionJsonPath,
                        System.Text.Json.JsonSerializer.Serialize(dict,
                            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

                    Log.Information("Completed stale session {Session}",
                        System.IO.Path.GetFileName(sessionDir));
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to complete stale session {Session}",
                    System.IO.Path.GetFileName(sessionDir));
            }

            // Step 2: Generate transcript.txt if all chunks are transcribed
            var transcriptPath = System.IO.Path.Combine(sessionDir, "transcript.txt");
            if (System.IO.File.Exists(transcriptPath)) continue;

            var cDir = System.IO.Path.Combine(sessionDir, "chunks");
            if (!System.IO.Directory.Exists(cDir)) continue;

            bool allDone = true;
            bool hasChunks = false;
            foreach (var chunkFile in System.IO.Directory.GetFiles(cDir, "chunk_*.json"))
            {
                var name = System.IO.Path.GetFileNameWithoutExtension(chunkFile);
                if (name.Contains("_loopback") || name.Contains("_mic")) continue;
                hasChunks = true;

                try
                {
                    using var cd = System.Text.Json.JsonDocument.Parse(
                        System.IO.File.ReadAllText(chunkFile));
                    var s = cd.RootElement.GetProperty("status").GetString();
                    if (s is not ("transcribed" or "failed"))
                    {
                        allDone = false;
                        break;
                    }
                }
                catch { allDone = false; break; }
            }

            if (!hasChunks || !allDone) continue;

            try
            {
                TranscriptGenerator.Generate(sessionDir);
                Log.Information("Auto-generated transcript for {Session}",
                    System.IO.Path.GetFileName(sessionDir));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to generate transcript for {Session}",
                    System.IO.Path.GetFileName(sessionDir));
            }
        }
    }

    private static RecorderConfig LoadConfig()
    {
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
