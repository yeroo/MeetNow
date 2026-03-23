using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.IO;
using System.Windows;

namespace MeetNow
{
    public partial class TeamsWebViewWindow : Window
    {
        private static readonly string UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "WebView2Profile");

        private bool _isInitialized;

        public TeamsWebViewWindow()
        {
            InitializeComponent();
        }

        public async void InitializeWebView()
        {
            if (_isInitialized) return;

            try
            {
                StatusBar.Text = "Creating WebView2 environment...";
                var env = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: UserDataFolder);

                await webView.EnsureCoreWebView2Async(env);

                // Spoof Edge user-agent to avoid Teams blocking embedded access
                var edgeVersion = env.BrowserVersionString;
                webView.CoreWebView2.Settings.UserAgent =
                    $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{edgeVersion} Safari/537.36 Edg/{edgeVersion}";

                webView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

                StatusBar.Text = "Navigating to Teams...";
                webView.CoreWebView2.Navigate("https://teams.microsoft.com");
                _isInitialized = true;

                Log.Information("WebView2 initialized, navigating to Teams");
            }
            catch (Exception ex)
            {
                StatusBar.Text = $"WebView2 init failed: {ex.Message}";
                Log.Error(ex, "Failed to initialize WebView2");
            }
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            var url = webView.CoreWebView2.Source;
            if (e.IsSuccess)
            {
                StatusBar.Text = $"Loaded: {url}";
                Log.Information("WebView2 navigation completed: {Url}", url);
            }
            else
            {
                StatusBar.Text = $"Navigation failed ({e.WebErrorStatus}): {url}";
                Log.Warning("WebView2 navigation failed: {Status} {Url}", e.WebErrorStatus, url);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Hide instead of close — re-show via tray menu
            e.Cancel = true;
            Hide();
        }

        public void DisposeWebView()
        {
            webView?.Dispose();
        }
    }
}
