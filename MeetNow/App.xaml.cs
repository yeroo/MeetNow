using Microsoft.Win32;
using Serilog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MeetNow
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var logFolder = Path.GetTempPath();
           
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                //.WriteTo.Console()
                .WriteTo.File(logFolder + @"\MeetNow.log",
                    rollingInterval: RollingInterval.Day,
                    rollOnFileSizeLimit: true)
                .CreateLogger();
            Log.Information("-----------------------");
            Log.Information($"MeetNow Started");

          
        }
    }
}
