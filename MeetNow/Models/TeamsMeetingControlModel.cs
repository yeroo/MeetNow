using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MeetNow
{
    internal class TeamsMeetingControlModel : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private string _username;
        public string Username
        {
            get => _username; set
            {
                if (value != _username)
                {
                    _username = value;
                    RaisePropertyChanged(nameof(Username));
                }
            }
        }
        private TeamsMeeting _teamsMeeting;
        public TeamsMeeting TeamsMeeting
        {
            get => _teamsMeeting; set
            {
                if (value != _teamsMeeting)
                {
                    _teamsMeeting = value;
                    RaisePropertyChanged(nameof(TeamsMeeting));
                }
            }
        }

        protected void RaisePropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
