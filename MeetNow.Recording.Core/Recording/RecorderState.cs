namespace MeetNow.Recording.Core.Recording;

public enum RecorderState
{
    Idle,
    Recording,
    MicKeepalive,
    Draining,
    Flushing
}
