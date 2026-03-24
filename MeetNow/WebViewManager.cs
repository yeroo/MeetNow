using Microsoft.Web.WebView2.Core;
using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MeetNow
{
    public class WebViewManager
    {
        private static readonly string UserDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MeetNow", "WebView2Profile");

        private static readonly Lazy<WebViewManager> _instance = new(() => new WebViewManager());
        public static WebViewManager Instance => _instance.Value;

        private CoreWebView2Environment? _environment;
        private WebViewInstance? _persistent;
        private WebViewInstance? _transient;
        private readonly SemaphoreSlim _transientLock = new(1, 1);
        private readonly ConcurrentDictionary<string, DispatcherTimer> _scheduledTasks = new();
        private DispatcherTimer? _heartbeatTimer;
        private DispatcherTimer? _transientIdleTimer;
        private bool _initialized;
        private bool _shuttingDown;
        private DateTime _authBackoffUntil = DateTime.MinValue;

        public bool IsInitialized => _initialized;
        public WebViewInstance? PersistentInstance => _persistent;
        public WebViewInstance? TransientInstance => _transient;

        public IReadOnlyList<WebViewInstance> ActiveInstances
        {
            get
            {
                var list = new List<WebViewInstance>();
                if (_persistent != null) list.Add(_persistent);
                if (_transient != null) list.Add(_transient);
                return list;
            }
        }

        private WebViewManager() { }

        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                _environment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: UserDataFolder);

                Log.Information("WebViewManager: shared environment created (Edge {Version})",
                    _environment.BrowserVersionString);

                _initialized = true;

                // Start persistent instance (MessageMonitor on Teams)
                await StartPersistentAsync("https://teams.microsoft.com");

                // Start heartbeat timer (60s)
                _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
                _heartbeatTimer.Tick += async (s, e) => await HeartbeatCheckAsync();
                _heartbeatTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WebViewManager: initialization failed");
            }
        }

        public async Task<WebViewInstance?> GetPersistentAsync(string url)
        {
            if (!_initialized || _environment == null) return null;

            if (_persistent != null) return _persistent;

            return await StartPersistentAsync(url);
        }

        private async Task<WebViewInstance?> StartPersistentAsync(string url)
        {
            if (_environment == null) return null;

            try
            {
                _persistent = new WebViewInstance("MessageMonitor", InstanceType.Persistent);
                await _persistent.InitializeAsync(_environment);
                await _persistent.NavigateAndWaitAsync(url);
                return _persistent;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WebViewManager: failed to start persistent instance");
                _persistent?.Dispose();
                _persistent = null;
                return null;
            }
        }

        public async Task<WebViewInstance?> AcquireTransientAsync(string url, int timeoutMs = 30000)
        {
            if (!_initialized || _environment == null) return null;

            // Auth backoff check
            if (DateTime.Now < _authBackoffUntil)
            {
                Log.Warning("WebViewManager: auth backoff active until {Until}", _authBackoffUntil);
                return null;
            }

            if (!await _transientLock.WaitAsync(timeoutMs))
            {
                Log.Warning("WebViewManager: transient slot busy, timed out after {Ms}ms", timeoutMs);
                return null;
            }

            try
            {
                // Stop idle timer if running
                _transientIdleTimer?.Stop();
                _transientIdleTimer = null;

                // Reuse existing or create new
                if (_transient == null)
                {
                    _transient = new WebViewInstance("Transient", InstanceType.Transient);
                    await _transient.InitializeAsync(_environment);
                }

                await _transient.NavigateAndWaitAsync(url);

                // Check for auth redirect
                if (_transient.CurrentUrl != null &&
                    _transient.CurrentUrl.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase))
                {
                    Log.Warning("WebViewManager: auth redirect detected, backing off 5 min");
                    _authBackoffUntil = DateTime.Now.AddMinutes(5);
                    ReleaseTransient();
                    return null;
                }

                return _transient;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "WebViewManager: failed to acquire transient instance");
                ReleaseTransient();
                return null;
            }
        }

        public void ReleaseTransient()
        {
            // Start idle timeout — dispose after 60s if not reacquired
            _transientIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _transientIdleTimer.Tick += (s, e) =>
            {
                _transientIdleTimer?.Stop();
                _transientIdleTimer = null;
                DisposeTransient();
            };
            _transientIdleTimer.Start();

            try { _transientLock.Release(); }
            catch (SemaphoreFullException) { }
        }

        private void DisposeTransient()
        {
            _transient?.Dispose();
            _transient = null;
            Log.Information("WebViewManager: transient instance disposed (idle timeout)");
        }

        public void ScheduleTask(string name, TimeSpan interval, Func<WebViewInstance?, Task> workFunc)
        {
            if (_scheduledTasks.ContainsKey(name))
            {
                Log.Warning("WebViewManager: task '{Name}' already scheduled", name);
                return;
            }

            var timer = new DispatcherTimer { Interval = interval };
            timer.Tick += async (s, e) =>
            {
                if (_shuttingDown) return;
                try
                {
                    await workFunc(_persistent);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "WebViewManager: scheduled task '{Name}' failed", name);
                }
            };
            timer.Start();
            _scheduledTasks[name] = timer;
            Log.Information("WebViewManager: scheduled task '{Name}' every {Interval}", name, interval);
        }

        public async Task RequestTask(string name, Func<WebViewInstance, Task> workFunc,
            string url = "https://teams.microsoft.com", CancellationToken ct = default)
        {
            Log.Information("WebViewManager: requesting transient task '{Name}'", name);

            var instance = await AcquireTransientAsync(url);
            if (instance == null)
            {
                Log.Warning("WebViewManager: could not acquire transient for task '{Name}'", name);
                return;
            }

            try
            {
                // Task timeout: 120s
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(120));

                await workFunc(instance);
            }
            catch (OperationCanceledException)
            {
                Log.Warning("WebViewManager: task '{Name}' timed out", name);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebViewManager: task '{Name}' failed", name);
            }
            finally
            {
                ReleaseTransient();
            }
        }

        private async Task HeartbeatCheckAsync()
        {
            if (_persistent == null || _shuttingDown) return;

            try
            {
                var alive = await _persistent.HeartbeatAsync();
                if (!alive)
                {
                    Log.Warning("WebViewManager: persistent instance heartbeat failed, recreating");
                    _persistent.Dispose();
                    _persistent = null;
                    await StartPersistentAsync("https://teams.microsoft.com");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebViewManager: heartbeat check failed");
            }
        }

        public void Shutdown()
        {
            _shuttingDown = true;

            _heartbeatTimer?.Stop();
            _transientIdleTimer?.Stop();

            foreach (var timer in _scheduledTasks.Values)
                timer.Stop();
            _scheduledTasks.Clear();

            _transient?.Dispose();
            _transient = null;

            _persistent?.Dispose();
            _persistent = null;

            _transientLock.Dispose();

            Log.Information("WebViewManager: shutdown complete");
        }
    }
}
