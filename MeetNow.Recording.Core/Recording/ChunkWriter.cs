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
            DurationSeconds = loopbackSamples.Length / (double)AudioFormat.SampleRate,
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
