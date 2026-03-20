using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace MeetNow
{
    public static class MeetNowHelper
    {
        public static void RestartApplication()
        {
            try
            {
                var exePath = GetExePath();
                if (exePath == null) return;

                Log.Information("Restarting MeetNow from {Path}", exePath);
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true });
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restart MeetNow");
                MessageBox.Show($"Failed to restart: {ex.Message}",
                    "MeetNow", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Kills Outlook, then launches a batch script that:
        /// 1. Starts Outlook
        /// 2. Waits 30 seconds for COM to register
        /// 3. Starts MeetNow
        /// Then exits the current MeetNow process.
        /// </summary>
        public static void RestartWithOutlook()
        {
            try
            {
                var exePath = GetExePath();
                if (exePath == null) return;

                var source = MeetNowSettings.Instance.OutlookSource;
                string outlookExe = source == "Classic" ? "outlook.exe" : "ms-outlook:";
                string outlookProcess = source == "Classic" ? "OUTLOOK" : "olk";
                int delaySec = source == "Classic" ? 30 : 10;

                // Kill Outlook if running
                foreach (var proc in Process.GetProcessesByName(outlookProcess))
                {
                    try
                    {
                        Log.Information("Killing {Process} (PID {Pid})", outlookProcess, proc.Id);
                        proc.Kill();
                        proc.WaitForExit(5000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "Failed to kill {Process}", outlookProcess);
                    }
                }

                // Write a launcher script that starts Outlook, waits, then starts MeetNow
                var scriptPath = Path.Combine(Path.GetTempPath(), "MeetNow_restart.cmd");
                var script = $"""
                    @echo off
                    echo Starting {(source == "Classic" ? "Classic" : "New")} Outlook...
                    start "" "{outlookExe}"
                    echo Waiting {delaySec} seconds for Outlook to initialize...
                    timeout /t {delaySec} /nobreak >nul
                    echo Starting MeetNow...
                    start "" "{exePath}"
                    del "%~f0"
                    """;
                File.WriteAllText(scriptPath, script);

                Log.Information("RestartWithOutlook: launching script {Script}", scriptPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = scriptPath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Minimized
                });

                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restart with Outlook");
                MessageBox.Show($"Failed to restart: {ex.Message}",
                    "MeetNow", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string? GetExePath()
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrEmpty(exePath))
            {
                Log.Error("Cannot restart: unable to determine executable path");
                MessageBox.Show("Cannot restart: unable to determine executable path.",
                    "MeetNow", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            return exePath;
        }
    }
}
