using System;
using System.Collections.Generic;
using System.Windows.Threading;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Services
{
    /// <summary>
    /// Manages automatic and manual snapshots of system state.
    ///
    /// Auto-snapshot:
    ///   When enabled in settings, takes a full snapshot every N minutes.
    ///   Timer is independent from UpdaterService timers — runs on its own
    ///   DispatcherTimer so it doesn't interfere with real-time polling.
    ///
    /// Manual snapshot:
    ///   UI can call TakeNow() at any time — e.g. before killing a process
    ///   or switching power mode, as a before/after record.
    ///
    /// All actual file I/O delegated to Utils/SnapshotManager.cs.
    /// This service owns the timer and the trigger logic only.
    /// </summary>
    public class SnapshotService : IDisposable
    {
        private readonly MetricsService _metricsService;
        private readonly DispatcherTimer _timer;
        private bool _disposed;

        // ─── Events ───────────────────────────────────────────────────────────

        /// <summary>Fired after every successful snapshot — UI can show a toast.</summary>
        public event EventHandler<SnapshotTakenEventArgs>? OnSnapshotTaken;

        // ─── Constructor ──────────────────────────────────────────────────────

        public SnapshotService(MetricsService metricsService)
        {
            _metricsService = metricsService;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMinutes(Config.Current.SnapshotIntervalMinutes),
            };
            _timer.Tick += OnTimerTick;

            if (Config.Current.SnapshotAutoSave)
            {
                _timer.Start();
                Logger.Info($"Auto-snapshot enabled — interval: " +
                            $"{Config.Current.SnapshotIntervalMinutes} min.");
            }
        }

        // ─── Public: Manual Trigger ───────────────────────────────────────────

        /// <summary>
        /// Takes a snapshot immediately — called by UI on demand.
        /// Also called before destructive actions (kill, mode switch) as a record.
        /// </summary>
        public bool TakeNow(
            IReadOnlyList<ProcessInfo>? processes = null,
            string? label = null)
        {
            try
            {
                var metrics = _metricsService.Latest;
                bool success;

                if (processes != null)
                    success = SnapshotManager.SaveFull(metrics, processes);
                else
                    success = SnapshotManager.SaveMetrics(metrics);

                if (success)
                {
                    Logger.Action($"Manual snapshot taken{(label != null ? $" [{label}]" : "")}.");
                    OnSnapshotTaken?.Invoke(this, new SnapshotTakenEventArgs
                    {
                        IsAutomatic = false,
                        Label = label ?? "Manual",
                        Metrics = metrics,
                        TakenAt = DateTime.UtcNow,
                    });
                }

                return success;
            }
            catch (Exception ex)
            {
                Logger.Error($"Manual snapshot failed: {ex.Message}");
                return false;
            }
        }

        // ─── Public: Auto-snapshot Control ───────────────────────────────────

        /// <summary>Enables auto-snapshots at the configured interval.</summary>
        public void EnableAuto(int intervalMinutes)
        {
            _timer.Stop();
            _timer.Interval = TimeSpan.FromMinutes(Math.Max(1, intervalMinutes));
            _timer.Start();

            Config.Current.SnapshotAutoSave = true;
            Config.Current.SnapshotIntervalMinutes = intervalMinutes;
            Config.Save();

            Logger.Info($"Auto-snapshot enabled at {intervalMinutes} min interval.");
        }

        /// <summary>Disables auto-snapshots.</summary>
        public void DisableAuto()
        {
            _timer.Stop();
            Config.Current.SnapshotAutoSave = false;
            Config.Save();
            Logger.Info("Auto-snapshot disabled.");
        }

        public bool IsAutoEnabled => _timer.IsEnabled;

        // ─── Public: History ──────────────────────────────────────────────────

        /// <summary>Returns list of all saved snapshots for the UI history view.</summary>
        public IReadOnlyList<SnapshotInfo> GetHistory()
            => SnapshotManager.ListSnapshots();

        /// <summary>Clears all saved snapshots from disk.</summary>
        public void ClearHistory()
        {
            SnapshotManager.ClearAll();
            Logger.Action("Snapshot history cleared.");
        }

        // ─── Private: Timer Tick ──────────────────────────────────────────────

        private void OnTimerTick(object? sender, EventArgs e)
        {
            try
            {
                var metrics = _metricsService.Latest;
                var success = SnapshotManager.SaveMetrics(metrics);

                if (success)
                {
                    Logger.Info("Auto-snapshot taken.");
                    OnSnapshotTaken?.Invoke(this, new SnapshotTakenEventArgs
                    {
                        IsAutomatic = true,
                        Label = "Auto",
                        Metrics = metrics,
                        TakenAt = DateTime.UtcNow,
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Auto-snapshot failed: {ex.Message}");
            }
        }

        // ─── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            Logger.Info("SnapshotService disposed.");
        }
    }

    // ─── Event Args ───────────────────────────────────────────────────────────

    public class SnapshotTakenEventArgs : EventArgs
    {
        public bool IsAutomatic { get; init; }
        public string Label { get; init; } = string.Empty;
        public SystemMetrics Metrics { get; init; } = null!;
        public DateTime TakenAt { get; init; }
    }
}