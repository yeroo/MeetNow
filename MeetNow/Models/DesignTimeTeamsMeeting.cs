using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace MeetNow
{
    public  class DesignTimeTeamsMeeting:TeamsMeeting
    {
        public DesignTimeTeamsMeeting()
        {
            Body = "[Daily (except Friday) portfolio sync] ";
            Categories = "Blue category";
            Start = DateTime.Now.AddSeconds(5);
            End = DateTime.Now.AddMinutes(30);
            IsRequired = true;
            Location = "Microsoft Teams Meeting";
            OptionalAttendees = new[] { "John Doe" };
            RequiredAttendees = new[] { "Patrick Brown" };
            Organizer = "Prescilla Mollin";
            Recurrent = true;
            ResponseStatus = ResponseStatus.olResponseAccepted;
            Subject = "Portfolio Sync (Daily)";
            TeamsUrl = @"https://teams.microsoft.com/l/meetup-join";


        }
    }
}
