using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MeetNow
{
    public partial class DebugViewerWindow : Window
    {
        private WebViewInstance? _currentlyShowing;

        public DebugViewerWindow()
        {
            InitializeComponent();
            RefreshInstances();
        }

        public void RefreshInstances()
        {
            InstanceCombo.Items.Clear();
            InstanceCombo.Items.Add("(none)");

            foreach (var instance in WebViewManager.Instance.ActiveInstances)
            {
                InstanceCombo.Items.Add(instance.Name);
            }

            InstanceCombo.SelectedIndex = 0;
        }

        private void InstanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Move previously shown instance back offscreen
            if (_currentlyShowing?.HostWindow != null)
            {
                _currentlyShowing.HostWindow.Left = -10000;
                _currentlyShowing.HostWindow.Top = -10000;
                _currentlyShowing = null;
            }

            var selected = InstanceCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || selected == "(none)") return;

            var instance = WebViewManager.Instance.ActiveInstances
                .FirstOrDefault(i => i.Name == selected);

            if (instance?.HostWindow != null)
            {
                // Move the instance host window on-screen, centered
                var screen = SystemParameters.WorkArea;
                instance.HostWindow.Left = (screen.Width - instance.HostWindow.Width) / 2;
                instance.HostWindow.Top = (screen.Height - instance.HostWindow.Height) / 2;
                instance.HostWindow.Topmost = true;
                instance.HostWindow.Topmost = false;
                _currentlyShowing = instance;
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Move any shown instance back offscreen before hiding
            if (_currentlyShowing?.HostWindow != null)
            {
                _currentlyShowing.HostWindow.Left = -10000;
                _currentlyShowing.HostWindow.Top = -10000;
                _currentlyShowing = null;
            }

            e.Cancel = true;
            Hide();
        }
    }
}
