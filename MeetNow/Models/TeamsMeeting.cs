using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MeetNow
{
    public enum ResponseStatus
    {
        olResponseNone = 0,
        olResponseOrganized = 1,
        olResponseTentative = 2,
        olResponseAccepted = 3,
        olResponseDeclined = 4,
        olResponseNotResponded = 5
    }
    public class TeamsMeeting
    {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Subject { get; set; }
        public string TeamsUrl { get; set; }
        public bool Recurrent { get; set; }
        public ResponseStatus ResponseStatus { get; set; }
        public string Location { get; set; }
        public string Organizer { get; set; }
        public bool IsRequired { get; set; }
        public string[] RequiredAttendees { get; set; }
        public string[] OptionalAttendees { get; set; }
        public string Body { get; set; }
        public string Categories { get; set; }
        public byte[] RTFBody { get; set; }

    }
}
