using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetNow.Recording.Core;

/// <summary>
/// Watches for audio device changes (connect/disconnect/default change).
/// Fires OnDefaultDeviceChanged when the default render or capture device changes.
/// </summary>
public class DeviceMonitor : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator;
    private readonly NotificationClient _client;

    public event Action<DataFlow, string>? OnDefaultDeviceChanged;

    public DeviceMonitor()
    {
        _enumerator = new MMDeviceEnumerator();
        _client = new NotificationClient(this);
        _enumerator.RegisterEndpointNotificationCallback(_client);
    }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(_client);
        _enumerator.Dispose();
    }

    private class NotificationClient : IMMNotificationClient
    {
        private readonly DeviceMonitor _owner;
        public NotificationClient(DeviceMonitor owner) => _owner = owner;

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (role == Role.Multimedia || role == Role.Communications)
            {
                _owner.OnDefaultDeviceChanged?.Invoke(flow, defaultDeviceId);
            }
        }

        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
