using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MeetNow
{
    /// <summary>
    /// Interaction logic for PopupEventsWindow.xaml
    /// </summary>
    public partial class PopupEventsWindow : Window
    {
        private static readonly List<PopupEventsWindow> _windows = new();
        public static void CloseAllWindows()
        {
            if (_windows.Count > 0)
            {
                _windows.ForEach(w => w.Close());
                _windows.Clear();
            }
            SfxHelper.StopAllDevices();
        }
        public static void Show(TeamsMeeting[] events)
        {
            CloseAllWindows();
            for (int i = 0; i < Screen.AllScreens.Length; i++)
            {
                var screen = Screen.AllScreens[i];
                PopupEventsWindow window = new PopupEventsWindow();
                _windows.Add(window);
                window.WindowState = WindowState.Normal;
                window.WindowStyle = WindowStyle.None;
                window.Left = screen.WorkingArea.Left;
                window.Top = screen.WorkingArea.Top;    
                window.Width = screen.WorkingArea.Width;
                window.Height = screen.WorkingArea.Height;
                window.Show();
                window.SetTeamsMeetings(events);
                window.Activate();
                window.Focus();
            }
            SfxHelper.PlayOnAllDevices();
        }
        public PopupEventsWindow()
        {
            InitializeComponent();
        }

        TeamsMeeting[] _teamsMeetings;
        public void SetTeamsMeetings(TeamsMeeting[] teamsMeetings)
        {
            _teamsMeetings = teamsMeetings;
            if (_teamsMeetings != null && _teamsMeetings.Count() > 0)
            {
                DivideRectangle(canvas, _teamsMeetings.Count());
            }
        }
        public TeamsMeeting[] GetTeamsMeetings()
        {
            return _teamsMeetings;
        }
        public void DivideRectangle(Canvas canvas, int n)
        {
            canvas.Children.Clear();
            double rectWidth = canvas.ActualWidth;
            double rectHeight = canvas.ActualHeight;

            int bestRows = 1;
            int bestCols = n;
            double bestSquareness = double.MaxValue;  // Initialize with a high value

            for (int potentialRows = 1; potentialRows <= n; potentialRows++)
            {
                int potentialCols = (int)Math.Ceiling((double)n / potentialRows);

                if (potentialRows * potentialCols < n)
                {
                    continue; // We need enough tiles.
                }

                double potentialWidth = rectWidth / potentialCols;
                double potentialHeight = rectHeight / potentialRows;

                double squareness = Math.Abs(potentialWidth / potentialHeight - 1);

                if (squareness < bestSquareness)
                {
                    bestSquareness = squareness;
                    bestRows = potentialRows;
                    bestCols = potentialCols;
                }
            }

            double width = rectWidth / bestCols;
            double height = rectHeight / bestRows;

            for (int i = 0; i < bestRows; i++)
            {
                for (int j = 0; j < bestCols; j++)
                {
                    if (i * bestCols + j >= n)
                    {
                        return;  // We've created all the pieces we need
                    }
                    var piece = new Viewbox()
                    {
                        Width = width,
                        Height = height,

                    };
                    var child = new TeamsMeetingControl()
                    {
                        DataContext = _teamsMeetings[i * bestCols + j]
                    };
                    piece.Child = child;
                   
                    Canvas.SetLeft(piece, j * width);
                    Canvas.SetTop(piece, i * height);

                    canvas.Children.Add(piece);
                }
            }
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            CloseAllWindows();
        }
    }
}
