
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MeetNow
{
    internal static class SfxHelper
    {
        static List<DirectSoundOut> playbacks = new();
        const string CHIME = "big_ben_2013.mp3";
        public static void PlayOnAllDevices()
        {
            
            var soundfile = Path.Combine(AppContext.BaseDirectory, "SFX", CHIME);

            if (!File.Exists(soundfile)) {
                Log.Information($"SFX {soundfile} is mussing");
                return;
            }
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
            var audioFile = new AudioFileReader(audioFilePath); // Supports WAV, MP3, and a few other formats
            output.Init(audioFile);
            output.Play();

            lock (playbacks)
            {
                playbacks.Add(output);
            }
            // Handle playback complete or dispose output when necessary
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
