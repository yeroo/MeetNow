using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MeetNow
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        static readonly string InstallDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetNow");

        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            _singleInstanceMutex = new Mutex(true, "MeetNow_SingleInstance_B7A3F2", out bool createdNew);
            if (!createdNew)
            {
                MessageBox.Show("MeetNow is already running.", "MeetNow", MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            base.OnStartup(e);
            var logFolder = Path.GetTempPath();

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                //.WriteTo.Console()
                .WriteTo.File(logFolder + @"\MeetNow.log",
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1))
                .CreateLogger();
            Log.Information("-----------------------");
            Log.Information("MeetNow Started (build {Build})", BuildInfo.Number);

#if !DEBUG
            if (SelfInstallIfNeeded())
            {
                Shutdown();
                return;
            }
#endif
        }

        /// <summary>
        /// If not running from the install directory, copies itself there,
        /// registers Windows startup, creates a desktop shortcut, and relaunches.
        /// Returns true if install was performed (caller should exit).
        /// </summary>
        private bool SelfInstallIfNeeded()
        {
            string currentExe = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (string.IsNullOrEmpty(currentExe))
                return false;

            string currentDir = Path.GetDirectoryName(currentExe)!;
            string installedExe = Path.Combine(InstallDir, "MeetNow.exe");

            // Already running from install directory
            if (string.Equals(currentDir, InstallDir, StringComparison.OrdinalIgnoreCase))
                return false;

            Log.Information($"Self-installing from {currentDir} to {InstallDir}");

            try
            {
                // Create install directory
                Directory.CreateDirectory(InstallDir);

                // Copy EXE (SFX audio is embedded as a resource)
                File.Copy(currentExe, installedExe, overwrite: true);

                // Register Windows startup (HKCU - no admin needed)
                using var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                key?.SetValue("MeetNow", installedExe);

                // Create desktop shortcut
                CreateDesktopShortcut(installedExe);

                Log.Information("Self-install complete. Launching from install directory.");

                // Launch from installed location
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExe,
                    UseShellExecute = true
                });

                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Self-install failed");
                MessageBox.Show(
                    $"Failed to install MeetNow:\n{ex.Message}\n\nThe app will run from the current location.",
                    "MeetNow", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }

        private static void CreateDesktopShortcut(string targetExe)
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string shortcutPath = Path.Combine(desktopPath, "MeetNow.lnk");

                // Use WScript.Shell COM to create .lnk
                var shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                dynamic shell = Activator.CreateInstance(shellType)!;
                dynamic shortcut = shell.CreateShortcut(shortcutPath);
                shortcut.TargetPath = targetExe;
                shortcut.WorkingDirectory = Path.GetDirectoryName(targetExe);
                shortcut.IconLocation = targetExe + ",0";
                shortcut.Description = "MeetNow - Teams Meeting Popup";
                shortcut.Save();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Could not create desktop shortcut");
            }
        }
    }
}
