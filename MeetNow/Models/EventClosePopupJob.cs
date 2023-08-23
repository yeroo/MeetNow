using FluentScheduler;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace MeetNow
{
    public class EventClosePopupJob : IJob
    {
        public void Execute()
        {
            Log.Information($"EventClosePopupJob.Execute");
            Application.Current.Dispatcher.Invoke(() =>
            {
                PopupEventsWindow.CloseAllWindows();
            });

        }
    }
}
