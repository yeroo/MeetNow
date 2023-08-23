using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MeetNow
{
    /// <summary>
    /// Interaction logic for TeamsMeetingControl.xaml
    /// </summary>
    public partial class TeamsMeetingControl : UserControl
    {
        public TeamsMeetingControl()
        {
            InitializeComponent();
        }

        private void JoinButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement button && button.Tag is string teamsUrl)
            {
                // Open Teams meeting URL
                OutlookHelper.StartTeamsMeeting(teamsUrl);
                // Close parent window
                PopupEventsWindow.CloseAllWindows();
            }
        }
    }
}
