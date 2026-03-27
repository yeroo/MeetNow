using System.Text.Json.Serialization;

namespace MeetNow.Recording.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter<SplitReason>))]
public enum SplitReason
{
    [JsonStringEnumMemberName("silence_timeout")]
    SilenceTimeout,
    [JsonStringEnumMemberName("max_duration")]
    MaxDuration,
    [JsonStringEnumMemberName("device_change")]
    DeviceChange,
    [JsonStringEnumMemberName("manual_stop")]
    ManualStop
}
