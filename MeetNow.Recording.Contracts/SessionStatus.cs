using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SessionStatus>))]
public enum SessionStatus
{
    [JsonStringEnumMemberName("recording")]
    Recording,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("transcribed")]
    Transcribed
}
