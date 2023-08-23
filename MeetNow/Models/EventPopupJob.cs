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
    public class EventPopupJob : IJob
    {
        private TeamsMeeting[] _events;

        public EventPopupJob(TeamsMeeting[] events)
        {
            _events = events;
        }

        public void Execute()
        {
            if (_events == null || _events.Length > 0)
            {
                Log.Information($"EventPopupJob.Execute {_events[0].Start}: {_events[0].Subject}");
                Application.Current.Dispatcher.Invoke(() =>
                {
                    PopupEventsWindow.Show(_events);
                });
            }
        }
    }
}
