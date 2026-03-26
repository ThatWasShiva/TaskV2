using System;
using System.Collections.Generic;

namespace TaskManager.Models
{
    /// <summary>
    /// A point-in-time snapshot of all system-wide hardware metrics.
    /// Produced by SystemInfo.cs, consumed by the UI and PowerEstimator.
    /// Intentionally a plain data object — no INotifyPropertyChanged here.
    /// The ViewModel holds the live version and fires its own change events.
    /// </summary>
    public class SystemMetrics
    {
        // ─── Timestamp ────────────────────────────────────────────────────────

        /// <summary>When this snapshot was captured (UTC)</summary>
        public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

        // ─── CPU ──────────────────────────────────────────────────────────────

        public float CpuPercent { get; init; }
        public float CpuBaseSpeedGHz { get; init; }
        public int CpuCoreCount { get; init; }
        public int CpuLogicalCount { get; init; }
        public float CpuTempCelsius { get; init; }  // -1 if unavailable

        /// <summary>Per-core usage percentages, index = core number</summary>
        public IReadOnlyList<float> CpuPerCorePercent { get; init; } = Array.Empty<float>();

        // ─── Memory ───────────────────────────────────────────────────────────

        public long RamTotalBytes { get; init; }
        public long RamUsedBytes { get; init; }
        public long RamAvailableBytes { get; init; }
        public float RamPercent { get; init; }

        // Derived — convenient for display
        public float RamTotalGB => RamTotalBytes / (1024f * 1024 * 1024);
        public float RamUsedGB => RamUsedBytes / (1024f * 1024 * 1024);
        public float RamAvailableGB => RamAvailableBytes / (1024f * 1024 * 1024);

        public long PageFileTotalBytes { get; init; }
        public long PageFileUsedBytes { get; init; }

        // ─── GPU ──────────────────────────────────────────────────────────────

        public float GpuPercent { get; init; }   // -1 if unavailable
        public float GpuTempCelsius { get; init; }   // -1 if unavailable
        public long GpuVramTotalBytes { get; init; }
        public long GpuVramUsedBytes { get; init; }
        public string GpuName { get; init; } = string.Empty;

        public float GpuVramTotalGB => GpuVramTotalBytes / (1024f * 1024 * 1024);
        public float GpuVramUsedGB => GpuVramUsedBytes / (1024f * 1024 * 1024);

        // ─── Disk ─────────────────────────────────────────────────────────────

        public float DiskReadMBps { get; init; }
        public float DiskWriteMBps { get; init; }
        public float DiskTotalMBps => DiskReadMBps + DiskWriteMBps;
        public float DiskPercent { get; init; }  // active time %

        // ─── Network ──────────────────────────────────────────────────────────

        public float NetworkUpMBps { get; init; }
        public float NetworkDownMBps { get; init; }
        public float NetworkTotalMBps => NetworkUpMBps + NetworkDownMBps;

        // ─── Battery ──────────────────────────────────────────────────────────

        public bool HasBattery { get; init; }
        public float BatteryPercent { get; init; }  // 0–100, -1 if no battery
        public bool IsCharging { get; init; }
        public float EstimatedWattsTotal { get; init; } // system-wide power draw

        // ─── System-wide Info ─────────────────────────────────────────────────

        public TimeSpan SystemUptime { get; init; }
        public int TotalProcesses { get; init; }
        public int TotalThreads { get; init; }
        public PowerMode CurrentPowerMode { get; init; }

        // ─── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a copy with a new timestamp — used by snapshot manager
        /// to freeze a moment-in-time record.
        /// </summary>
        public SystemMetrics WithTimestamp(DateTime at) =>
            this with { CapturedAt = at };
    }

    // ─── Supporting Enums ─────────────────────────────────────────────────────

    public enum PowerMode
    {
        Performance,
        Balanced,
        Efficiency
    }
}