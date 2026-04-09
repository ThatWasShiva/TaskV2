using System;
using System.Collections.Generic;
using TaskManager.Core;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Services
{
    /// <summary>
    /// Owns the live system metrics state and exposes targeted reads
    /// for each refresh tier.
    ///
    /// Why this exists separately from SystemInfo:
    ///   SystemInfo is a low-level hardware collector — it knows how to
    ///   talk to WMI, PerformanceCounters, and Hardware.Info.
    ///   MetricsService owns the *state* — the latest snapshot, sparkline
    ///   history, and per-tier accessors that UpdaterService calls.
    ///
    ///   This keeps SystemInfo focused on data collection and lets
    ///   MetricsService handle caching, history, and derived state.
    /// </summary>
    public class MetricsService
    {
        private readonly SystemInfo _systemInfo;

        // ─── Live State ───────────────────────────────────────────────────────

        private SystemMetrics _latest = new();
        private PowerMode _currentMode = PowerMode.Balanced;

        // ─── Sparkline History ────────────────────────────────────────────────
        // Fixed-length circular buffers — oldest entry replaced when full.

        private readonly FixedQueue<float> _cpuHistory;
        private readonly FixedQueue<float> _ramHistory;
        private readonly FixedQueue<float> _gpuHistory;
        private readonly FixedQueue<float> _netHistory;

        // ─── Constructor ──────────────────────────────────────────────────────

        public MetricsService(SystemInfo systemInfo)
        {
            _systemInfo = systemInfo;

            int histLen = Config.Current.SparklineHistory;
            _cpuHistory = new FixedQueue<float>(histLen);
            _ramHistory = new FixedQueue<float>(histLen);
            _gpuHistory = new FixedQueue<float>(histLen);
            _netHistory = new FixedQueue<float>(histLen);

            Logger.Info("MetricsService initialised.");
        }

        // ─── Public: Targeted Reads (called by UpdaterService per tier) ───────

        /// <summary>Fast tier — CPU percent only, via PerformanceCounter.</summary>
        public float GetCpuPercent() => PerfCounterPool.CpuTotal();

        /// <summary>Fast tier — RAM percent derived from available bytes.</summary>
        public float GetRamPercent()
        {
            float available = PerfCounterPool.RamAvailableBytes();
            float total = _latest.RamTotalBytes > 0
                              ? _latest.RamTotalBytes
                              : 16L * 1024 * 1024 * 1024; // fallback 16 GB
            return (1f - available / total) * 100f;
        }

        /// <summary>
        /// Medium tier — full snapshot via SystemInfo.
        /// Also pushes values into sparkline history.
        /// </summary>
        public SystemMetrics GetSnapshot()
        {
            _latest = _systemInfo.GetSnapshot(_currentMode);

            // Push into sparkline history
            _cpuHistory.Enqueue(_latest.CpuPercent);
            _ramHistory.Enqueue(_latest.RamPercent);
            _gpuHistory.Enqueue(_latest.GpuPercent > 0 ? _latest.GpuPercent : 0f);
            _netHistory.Enqueue(_latest.NetworkTotalMBps);

            return _latest;
        }

        /// <summary>Slow tier — temperatures only.</summary>
        public (float cpu, float gpu) GetTemperatures()
            => (_latest.CpuTempCelsius, _latest.GpuTempCelsius);

        /// <summary>Slow tier — battery state only.</summary>
        public (bool hasBattery, float percent, bool isCharging) GetBattery()
            => (_latest.HasBattery, _latest.BatteryPercent, _latest.IsCharging);

        // ─── Public: Latest Snapshot ──────────────────────────────────────────

        /// <summary>Returns the most recently collected full snapshot.</summary>
        public SystemMetrics Latest => _latest;

        // ─── Public: Sparkline History ────────────────────────────────────────

        public IReadOnlyList<float> CpuHistory => _cpuHistory.ToList();
        public IReadOnlyList<float> RamHistory => _ramHistory.ToList();
        public IReadOnlyList<float> GpuHistory => _gpuHistory.ToList();
        public IReadOnlyList<float> NetHistory => _netHistory.ToList();

        // ─── Public: Power Mode ───────────────────────────────────────────────

        /// <summary>
        /// Tells MetricsService which power mode is active.
        /// Passed into SystemInfo.GetSnapshot() so it's included in each snapshot.
        /// </summary>
        public void SetPowerMode(PowerMode mode)
        {
            _currentMode = mode;
            Logger.Debug($"MetricsService power mode updated to {mode}.");
        }
    }

    // ─── Fixed-Length Queue ───────────────────────────────────────────────────

    /// <summary>
    /// Circular buffer that holds a fixed number of items.
    /// When full, oldest item is dropped on Enqueue.
    /// Used for sparkline history — no unbounded growth.
    /// </summary>
    internal class FixedQueue<T>
    {
        private readonly Queue<T> _inner;
        private readonly int _capacity;

        public FixedQueue(int capacity)
        {
            _capacity = capacity;
            _inner = new Queue<T>(capacity);
        }

        public void Enqueue(T item)
        {
            if (_inner.Count >= _capacity)
                _inner.Dequeue(); // drop oldest
            _inner.Enqueue(item);
        }

        public IReadOnlyList<T> ToList()
            => new List<T>(_inner);

        public int Count => _inner.Count;
    }
}