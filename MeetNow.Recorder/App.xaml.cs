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
