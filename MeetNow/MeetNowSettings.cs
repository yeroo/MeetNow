using Serilog;
using System;
using System.IO;
using System.Text.Json;

namespace MeetNow
{
    public class MeetNowSettings
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "settings.json");

        private static MeetNowSettings? _instance;

        // Autopilot behavior
        public bool ForwardUrgentInAutopilot { get; set; }
        public bool SimulateTypingInAutopilot { get; set; } = true;
        public bool AutoReplyHiInAutopilot { get; set; }
        public string? ForwardToEmail { get; set; }

        // Outlook source: "New" or "Classic"
        public string OutlookSource { get; set; } = "New";

        // Timings
        public string AutopilotOffTime { get; set; } = "18:00";
        public int TeamsOperationDelaySeconds { get; set; } = 10;
        public int SimulateTypingDurationSeconds { get; set; } = 30;
        public int SimulateTypingCooldownMinutes { get; set; } = 3;
        public int AutoReplyDelayMinutes { get; set; } = 10;
        public int AutoReplyMessageThreshold { get; set; } = 3;

        public static MeetNowSettings Instance => _instance ??= Load();

        private static MeetNowSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    return JsonSerializer.Deserialize<MeetNowSettings>(json) ?? new MeetNowSettings();
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to load settings");
            }
            return new MeetNowSettings();
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
                Log.Information("Settings saved");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save settings");
            }
        }
    }
}
