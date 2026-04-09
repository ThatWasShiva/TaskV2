using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using TaskManager.Core;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Services
{
    /// <summary>
    /// Central polling engine — owns all three refresh timers and coordinates
    /// data collection across SystemInfo, ProcessManager, and PowerEstimator.
    ///
    /// Tiered refresh strategy (from efficiency decisions):
    ///   Fast   (1s)  → CPU%, RAM%, active process CPU — feels "live"
    ///   Medium (3s)  → Full process list, disk I/O, network, power impact
    ///   Slow   (10s) → GPU temp, battery, startup entries, WMI cached data
    ///
    /// Threading model:
    ///   - Data collection runs on background threads (Task.Run)
    ///   - UI updates dispatched back to WPF dispatcher thread
    ///   - Timers are DispatcherTimers — fire on UI thread, offload work immediately
    ///
    /// Pause behaviour:
    ///   - All timers paused when window is minimized
    ///   - Resumed on restore — no stale data shown
    /// </summary>
    public class UpdaterService : IDisposable
    {
        // ─── Dependencies ─────────────────────────────────────────────────────

        private readonly SystemInfo _systemInfo;
        private readonly ProcessManager _processManager;
        private readonly PowerEstimator _powerEstimator;
        private readonly Scheduler _scheduler;
        private readonly MetricsService _metricsService;

        // ─── Timers ───────────────────────────────────────────────────────────

        private readonly DispatcherTimer _fastTimer;
        private readonly DispatcherTimer _mediumTimer;
        private readonly DispatcherTimer _slowTimer;

        // ─── State ────────────────────────────────────────────────────────────

        private bool _isPaused;
        private bool _disposed;

        // Guard against overlapping ticks if a collection takes longer than interval
        private int _fastRunning;
        private int _mediumRunning;
        private int _slowRunning;

        // ─── Events ───────────────────────────────────────────────────────────

        /// <summary>Fired on fast tick — CPU%, RAM% updated</summary>
        public event EventHandler<FastMetricsEventArgs>? OnFastUpdate;

        /// <summary>Fired on medium tick — full process list + metrics snapshot</summary>
        public event EventHandler<MediumUpdateEventArgs>? OnMediumUpdate;

        /// <summary>Fired on slow tick — temps, battery, GPU</summary>
        public event EventHandler<SlowMetricsEventArgs>? OnSlowUpdate;

        /// <summary>Fired when any error occurs during polling</summary>
        public event EventHandler<string>? OnPollingError;

        // ─── Constructor ──────────────────────────────────────────────────────

        public UpdaterService(
            SystemInfo systemInfo,
            ProcessManager processManager,
            PowerEstimator powerEstimator,
            Scheduler scheduler,
            MetricsService metricsService)
        {
            _systemInfo = systemInfo;
            _processManager = processManager;
            _powerEstimator = powerEstimator;
            _scheduler = scheduler;
            _metricsService = metricsService;

            _fastTimer = CreateTimer(Config.Current.RefreshFastMs, OnFastTick);
            _mediumTimer = CreateTimer(Config.Current.RefreshMediumMs, OnMediumTick);
            _slowTimer = CreateTimer(Config.Current.RefreshSlowMs, OnSlowTick);
        }

        // ─── Public: Control ──────────────────────────────────────────────────

        /// <summary>
        /// Starts all three polling timers.
        /// Also fires an immediate medium tick so UI has data on first render.
        /// </summary>
        public void Start()
        {
            Logger.Info("UpdaterService starting.");
            _fastTimer.Start();
            _mediumTimer.Start();
            _slowTimer.Start();

            // Fire once immediately so UI isn't blank on first render
            _ = Task.Run(CollectMediumAsync);
        }

        /// <summary>Stops all timers — no further ticks until Start() is called.</summary>
        public void Stop()
        {
            Logger.Info("UpdaterService stopped.");
            _fastTimer.Stop();
            _mediumTimer.Stop();
            _slowTimer.Stop();
        }

        /// <summary>
        /// Pauses all timers — used when window is minimized.
        /// Does not reset timer state — resumes from where it left off.
        /// </summary>
        public void Pause()
        {
            if (_isPaused) return;
            _isPaused = true;
            _fastTimer.Stop();
            _mediumTimer.Stop();
            _slowTimer.Stop();
            Logger.Debug("UpdaterService paused (window minimized).");
        }

        /// <summary>
        /// Resumes all timers after a Pause().
        /// Fires an immediate medium tick to refresh stale data.
        /// </summary>
        public void Resume()
        {
            if (!_isPaused) return;
            _isPaused = false;
            _fastTimer.Start();
            _mediumTimer.Start();
            _slowTimer.Start();
            _ = Task.Run(CollectMediumAsync); // refresh immediately on restore
            Logger.Debug("UpdaterService resumed.");
        }

        /// <summary>Returns true if the service is currently running (not paused/stopped).</summary>
        public bool IsRunning => _fastTimer.IsEnabled && !_isPaused;

        // ─── Private: Timer Callbacks ─────────────────────────────────────────

        private async void OnFastTick(object? sender, EventArgs e)
        {
            // Skip if previous fast tick is still running
            if (Interlocked.CompareExchange(ref _fastRunning, 1, 0) != 0) return;

            try
            {
                await Task.Run(CollectFastAsync);
            }
            catch (Exception ex)
            {
                Logger.Error($"Fast tick error: {ex.Message}");
                OnPollingError?.Invoke(this, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _fastRunning, 0);
            }
        }

        private async void OnMediumTick(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _mediumRunning, 1, 0) != 0) return;

            try
            {
                await Task.Run(CollectMediumAsync);
            }
            catch (Exception ex)
            {
                Logger.Error($"Medium tick error: {ex.Message}");
                OnPollingError?.Invoke(this, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _mediumRunning, 0);
            }
        }

        private async void OnSlowTick(object? sender, EventArgs e)
        {
            if (Interlocked.CompareExchange(ref _slowRunning, 1, 0) != 0) return;

            try
            {
                await Task.Run(CollectSlowAsync);
            }
            catch (Exception ex)
            {
                Logger.Error($"Slow tick error: {ex.Message}");
                OnPollingError?.Invoke(this, ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _slowRunning, 0);
            }
        }

        // ─── Private: Data Collection ─────────────────────────────────────────

        /// <summary>
        /// Fast tick — collect only CPU% and RAM%.
        /// Lightweight: two PerformanceCounter reads, no WMI.
        /// </summary>
        private void CollectFastAsync()
        {
            var cpu = PerfCounterPool.CpuTotal();
            var ram = _metricsService.GetRamPercent();

            DispatchToUI(() => OnFastUpdate?.Invoke(this, new FastMetricsEventArgs
            {
                CpuPercent = cpu,
                RamPercent = ram,
                Timestamp = DateTime.UtcNow,
            }));
        }

        /// <summary>
        /// Medium tick — full process list refresh + complete metrics snapshot.
        /// Runs on background thread, dispatches results to UI thread.
        /// </summary>
        private void CollectMediumAsync()
        {
            // 1. Collect fresh process list
            var processes = _processManager.GetAll();

            // 2. Estimate power impact for each process
            _powerEstimator.EstimateAll((System.Collections.Generic.IList<ProcessInfo>)processes);

            // 3. Get full system metrics snapshot
            var metrics = _metricsService.GetSnapshot();

            // 4. Run scheduler (check alert rules)
            _scheduler.Evaluate(metrics);

            DispatchToUI(() => OnMediumUpdate?.Invoke(this, new MediumUpdateEventArgs
            {
                Processes = processes,
                Metrics = metrics,
                Timestamp = DateTime.UtcNow,
            }));
        }

        /// <summary>
        /// Slow tick — temps, battery, GPU.
        /// These change slowly so 10s polling is sufficient.
        /// </summary>
        private void CollectSlowAsync()
        {
            var temps = _metricsService.GetTemperatures();
            var battery = _metricsService.GetBattery();

            DispatchToUI(() => OnSlowUpdate?.Invoke(this, new SlowMetricsEventArgs
            {
                CpuTempCelsius = temps.cpu,
                GpuTempCelsius = temps.gpu,
                BatteryPercent = battery.percent,
                IsCharging = battery.isCharging,
                HasBattery = battery.hasBattery,
                Timestamp = DateTime.UtcNow,
            }));
        }

        // ─── Private: Helpers ─────────────────────────────────────────────────

        private static DispatcherTimer CreateTimer(int intervalMs, EventHandler handler)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(intervalMs),
            };
            timer.Tick += handler;
            return timer;
        }

        /// <summary>
        /// Dispatches an action to the WPF UI thread.
        /// All event firing must go through here — WPF controls are not thread-safe.
        /// </summary>
        private static void DispatchToUI(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher == null) return;

            if (app.Dispatcher.CheckAccess())
                action();
            else
                app.Dispatcher.InvokeAsync(action, DispatcherPriority.Background);
        }

        // ─── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
            Logger.Info("UpdaterService disposed.");
        }
    }

    // ─── Event Args ───────────────────────────────────────────────────────────

    public class FastMetricsEventArgs : EventArgs
    {
        public float CpuPercent { get; init; }
        public float RamPercent { get; init; }
        public DateTime Timestamp { get; init; }
    }

    public class MediumUpdateEventArgs : EventArgs
    {
        public System.Collections.Generic.IReadOnlyList<ProcessInfo> Processes { get; init; } = null!;
        public SystemMetrics Metrics { get; init; } = null!;
        public DateTime Timestamp { get; init; }
    }

    public class SlowMetricsEventArgs : EventArgs
    {
        public float CpuTempCelsius { get; init; }
        public float GpuTempCelsius { get; init; }
        public float BatteryPercent { get; init; }
        public bool IsCharging { get; init; }
        public bool HasBattery { get; init; }
        public DateTime Timestamp { get; init; }
    }
}