using Serilog;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace MeetNow
{
    /// <summary>
    /// Sequential queue for Teams UI automation operations.
    /// Ensures only one operation runs at a time with configurable delays between them.
    /// Tracks per-contact cooldowns for simulate typing.
    /// </summary>
    public static class TeamsOperationQueue
    {
        public record QueueEntry(int Id, string Description, DateTime EnqueuedAt);

        private record TeamsOperation(int Id, string Description, Func<Task> Action);

        private static Channel<TeamsOperation> _channel =
            Channel.CreateUnbounded<TeamsOperation>(new UnboundedChannelOptions { SingleReader = true });

        private static readonly ConcurrentDictionary<string, DateTime> _lastSimulationTime = new();

        private static readonly List<QueueEntry> _pending = new();
        private static QueueEntry? _current;
        private static readonly object _lock = new();
        private static int _nextId;
        private static readonly HashSet<int> _skipped = new();

        public static event Action? QueueChanged;

        private static void FireQueueChanged()
        {
            try { QueueChanged?.Invoke(); }
            catch (Exception ex) { Log.Error(ex, "Queue: error in QueueChanged handler"); }
        }

        public static QueueEntry? Current
        {
            get { lock (_lock) return _current; }
        }

        /// <summary>True while an operation is actively running (not during the inter-op delay).</summary>
        public static bool IsExecuting { get; private set; }

        private static string? _currentStep;
        public static string? CurrentStep
        {
            get { lock (_lock) return _currentStep; }
            set { lock (_lock) _currentStep = value; FireQueueChanged(); }
        }

        public static List<QueueEntry> PendingSnapshot
        {
            get { lock (_lock) return _pending.ToList(); }
        }

        static TeamsOperationQueue()
        {
            Task.Run(ProcessQueueAsync);
        }

        /// <summary>
        /// Enqueue a Teams operation. It will execute after all prior operations complete,
        /// with a configurable delay between operations.
        /// </summary>
        public static void Enqueue(string description, Func<Task> action)
        {
            var id = Interlocked.Increment(ref _nextId);
            Log.Information("Queue: enqueued '{Description}' (id={Id})", description, id);
            lock (_lock)
                _pending.Add(new QueueEntry(id, description, DateTime.Now));
            _channel.Writer.TryWrite(new TeamsOperation(id, description, action));
            FireQueueChanged();
        }

        /// <summary>
        /// Check if simulate typing should run for this sender based on cooldown.
        /// Returns true and updates the timestamp if cooldown has passed.
        /// </summary>
        public static bool TryClaimSimulateTyping(string sender)
        {
            var cooldown = TimeSpan.FromMinutes(MeetNowSettings.Instance.SimulateTypingCooldownMinutes);
            var now = DateTime.Now;

            if (_lastSimulationTime.TryGetValue(sender, out var lastTime))
            {
                if (now - lastTime < cooldown)
                {
                    Log.Information("Queue: skipping simulate typing for {Sender}, last was {Ago:F0}s ago (cooldown {Cooldown}min)",
                        sender, (now - lastTime).TotalSeconds, cooldown.TotalMinutes);
                    return false;
                }
            }

            _lastSimulationTime[sender] = now;
            return true;
        }

        /// <summary>
        /// Clear all cooldown tracking (e.g. when autopilot is disabled).
        /// </summary>
        public static void ResetCooldowns()
        {
            _lastSimulationTime.Clear();
        }

        /// <summary>
        /// Clear all pending operations from the queue. The currently executing operation
        /// will finish, but everything else is dropped.
        /// </summary>
        public static void ClearQueue()
        {
            lock (_lock)
            {
                foreach (var entry in _pending)
                    _skipped.Add(entry.Id);
                _pending.Clear();
            }
            Log.Information("Queue: cleared all pending operations");
            FireQueueChanged();
        }

        private static async Task ProcessQueueAsync()
        {
            await foreach (var op in _channel.Reader.ReadAllAsync())
            {
                bool skip;
                lock (_lock)
                {
                    skip = _skipped.Remove(op.Id);
                    _pending.RemoveAll(e => e.Id == op.Id);
                    if (!skip)
                        _current = new QueueEntry(op.Id, op.Description, DateTime.Now);
                }
                if (skip)
                {
                    Log.Information("Queue: skipping cleared operation '{Description}'", op.Description);
                    continue;
                }
                FireQueueChanged();

                try
                {
                    Log.Information("Queue: executing '{Description}'", op.Description);
                    IsExecuting = true;
                    FireQueueChanged();
                    await op.Action();
                    IsExecuting = false;
                    FireQueueChanged();

                    var delay = TimeSpan.FromSeconds(MeetNowSettings.Instance.TeamsOperationDelaySeconds);
                    Log.Information("Queue: waiting {Delay}s before next operation", delay.TotalSeconds);
                    await Task.Delay(delay);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Queue: error executing '{Description}'", op.Description);
                }
                finally
                {
                    IsExecuting = false;
                    lock (_lock)
                    {
                        _current = null;
                        _currentStep = null;
                    }
                    FireQueueChanged();
                }
            }
        }
    }
}
