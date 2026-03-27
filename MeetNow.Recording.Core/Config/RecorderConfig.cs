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
