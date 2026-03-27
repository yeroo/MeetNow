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
