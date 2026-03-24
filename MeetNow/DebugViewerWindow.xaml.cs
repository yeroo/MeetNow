using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MeetNow
{
    public partial class DebugViewerWindow : Window
    {
        private WebViewInstance? _currentInstance;

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
            // Return previous WebView2 to its offscreen host
            DetachCurrent();

            var selected = InstanceCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(selected) || selected == "(none)")
            {
                ContentArea.Child = new TextBlock
                {
                    Text = "Select an instance to inspect",
                    Foreground = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(0x55, 0x55, 0x55)),
                    FontSize = 14,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                StatusText.Text = "";
                return;
            }

            var instance = WebViewManager.Instance.ActiveInstances
                .FirstOrDefault(i => i.Name == selected);

            if (instance?.HostWindow == null)
            {
                StatusText.Text = "Instance not available";
                return;
            }

            // Steal the WebView2 from the offscreen host window
            var webView = instance.HostWindow.Content as UIElement;
            if (webView != null)
            {
                instance.HostWindow.Content = null; // detach from host
                ContentArea.Child = webView;        // attach here
                _currentInstance = instance;
                StatusText.Text = instance.CurrentUrl ?? "Loading...";
            }
        }

        private void DetachCurrent()
        {
            if (_currentInstance?.HostWindow == null) return;

            // Return WebView2 to its offscreen host window
            var webView = ContentArea.Child;
            ContentArea.Child = null;
            _currentInstance.HostWindow.Content = webView;
            _currentInstance = null;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            DetachCurrent();
            InstanceCombo.SelectedIndex = 0;
            e.Cancel = true;
            Hide();
        }
    }
}
