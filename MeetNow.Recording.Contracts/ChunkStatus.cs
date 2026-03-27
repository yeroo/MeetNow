using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<ChunkStatus>))]
public enum ChunkStatus
{
    [JsonStringEnumMemberName("pending_transcription")]
    PendingTranscription,
    [JsonStringEnumMemberName("transcribing")]
    Transcribing,
    [JsonStringEnumMemberName("transcribed")]
    Transcribed,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("archived")]
    Archived
}
