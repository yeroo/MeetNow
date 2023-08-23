using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MeetNow
{
    internal class MainWindowModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _username;
        public string Username
        {
            get => _username; set
            {
                _username = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Username)));
            }
        }

        private TeamsMeeting[] _teamMeetings = new TeamsMeeting[] {};

        public TeamsMeeting[] TeamsMeetings
        {
            get => _teamMeetings; set
            {
                _teamMeetings = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TeamsMeetings)));
            }
        }
    }
}
