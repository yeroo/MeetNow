
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MeetNow
{
    internal static class SfxHelper
    {
        static List<DirectSoundOut> playbacks = new();
        const string CHIME_RESOURCE = "MeetNow.SFX.big_ben_2013.mp3";
        static string? _cachedChimePath;

        /// <summary>
        /// Extracts the embedded chime to a temp file (once) and returns the path.
        /// </summary>
        static string? GetChimePath()
        {
            if (_cachedChimePath != null && File.Exists(_cachedChimePath))
                return _cachedChimePath;

            try
            {
                using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(CHIME_RESOURCE);
                if (stream == null)
                {
                    Log.Information("Embedded resource {Resource} not found", CHIME_RESOURCE);
                    return null;
                }

                var tempPath = Path.Combine(Path.GetTempPath(), "MeetNow_chime.mp3");
                using var fileStream = File.Create(tempPath);
                stream.CopyTo(fileStream);
                _cachedChimePath = tempPath;
                return _cachedChimePath;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to extract chime resource");
                return null;
            }
        }

        public static void PlayOnAllDevices()
        {
            var soundfile = GetChimePath();
            if (soundfile == null)
                return;

            var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);

            foreach (var device in devices)
            {
                PlayOnDevice(device, soundfile);
            }
        }
        public static void StopAllDevices()
        {
            lock (playbacks)
            {
                foreach (var playback in playbacks)
                {
                    playback.Stop();
                }
            }
        }
        static void PlayOnDevice(MMDevice device, string audioFilePath)
        {
            var dsDevices = DirectSoundOut.Devices;
            var matchedDevice = dsDevices.FirstOrDefault(ds => ds.Description == device.FriendlyName || ds.ModuleName == device.FriendlyName);

            if (matchedDevice == null)
            {
                Log.Information($"Couldn't find a matching DirectSound device for {device.FriendlyName}");
                return;
            }
            var output = new DirectSoundOut(matchedDevice.Guid);
            var audioFile = new AudioFileReader(audioFilePath);
            output.Init(audioFile);
            output.Play();

            lock (playbacks)
            {
                playbacks.Add(output);
            }
            output.PlaybackStopped += (s, e) =>
            {
                output.Dispose();
                audioFile.Dispose();
                lock (playbacks)
                {
                    playbacks.Remove(output);
                }
            };
        }

    }
}
