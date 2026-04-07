using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Hardware.Info;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Collects system-wide hardware metrics — CPU, RAM, GPU, disk, network, battery.
    /// Produces a SystemMetrics snapshot consumed by the UI and PowerEstimator.
    ///
    /// Design rules:
    ///   - PerformanceCounters for hot path (1s polling) via PerfCounterPool
    ///   - WMI only for slow/cached data (temps, GPU name, battery)
    ///   - All WMI results cached — never called every tick
    ///   - Returns -1 for unavailable metrics rather than throwing
    /// </summary>
    public class SystemInfo : IDisposable
    {
        // ─── WMI Cache ────────────────────────────────────────────────────────
        // WMI queries are expensive. Cache results and refresh only on slow timer.

        private string _gpuName = string.Empty;
        private long _gpuVramTotal;
        private int _cpuCoreCount;
        private int _cpuLogicalCount;
        private float _cpuBaseSpeedGHz;
        private long _ramTotalBytes;
        private DateTime _lastSlowRefresh = DateTime.MinValue;

        // Network adapter name — detected once at startup
        private readonly string? _networkAdapter;

        // IHardwareInfo for temps (Hardware.Info NuGet)
        private readonly IHardwareInfo _hardwareInfo = new HardwareInfo();

        // ─── Constructor ──────────────────────────────────────────────────────

        public SystemInfo()
        {
            _networkAdapter = Helpers.GetActiveNetworkAdapterName();
            RefreshSlowData(); // populate cached fields immediately
            Logger.Info($"SystemInfo initialised. Adapter: {_networkAdapter ?? "none"}");
        }

        // ─── Public: Snapshot ─────────────────────────────────────────────────

        /// <summary>
        /// Builds and returns a full SystemMetrics snapshot.
        /// Call this on the medium timer (every 3s) — fast fields update every 1s
        /// via the lightweight UpdateFastMetrics() call.
        /// </summary>
        public SystemMetrics GetSnapshot(PowerMode currentMode)
        {
            // Refresh slow data (WMI) only every 10 seconds
            if ((DateTime.Now - _lastSlowRefresh).TotalSeconds >= 10)
                RefreshSlowData();

            var cpu = GetCpuMetrics();
            var ram = GetRamMetrics();
            var gpu = GetGpuMetrics();
            var disk = GetDiskMetrics();
            var network = GetNetworkMetrics();
            var battery = GetBatteryMetrics();

            return new SystemMetrics
            {
                CapturedAt = DateTime.UtcNow,

                // CPU
                CpuPercent = cpu.total,
                CpuBaseSpeedGHz = _cpuBaseSpeedGHz,
                CpuCoreCount = _cpuCoreCount,
                CpuLogicalCount = _cpuLogicalCount,
                CpuTempCelsius = cpu.temp,
                CpuPerCorePercent = cpu.perCore,

                // RAM
                RamTotalBytes = _ramTotalBytes,
                RamUsedBytes = ram.used,
                RamAvailableBytes = ram.available,
                RamPercent = ram.percent,

                // GPU
                GpuPercent = gpu.percent,
                GpuTempCelsius = gpu.temp,
                GpuVramTotalBytes = _gpuVramTotal,
                GpuVramUsedBytes = gpu.vramUsed,
                GpuName = _gpuName,

                // Disk
                DiskReadMBps = disk.readMBps,
                DiskWriteMBps = disk.writeMBps,
                DiskPercent = disk.activePercent,

                // Network
                NetworkUpMBps = network.upMBps,
                NetworkDownMBps = network.downMBps,

                // Battery
                HasBattery = battery.hasBattery,
                BatteryPercent = battery.percent,
                IsCharging = battery.isCharging,
                EstimatedWattsTotal = EstimateTotalWatts(cpu.total, gpu.percent),

                // System
                SystemUptime = GetSystemUptime(),
                TotalProcesses = System.Diagnostics.Process.GetProcesses().Length,
                TotalThreads = GetTotalThreads(),
                CurrentPowerMode = currentMode,
            };
        }

        // ─── CPU ──────────────────────────────────────────────────────────────

        private (float total, float temp, IReadOnlyList<float> perCore) GetCpuMetrics()
        {
            float total = PerfCounterPool.CpuTotal();

            // Per-core readings
            var perCore = new List<float>();
            for (int i = 0; i < _cpuLogicalCount; i++)
                perCore.Add(PerfCounterPool.CpuCore(i));

            float temp = GetCpuTemp();

            return (total, temp, perCore);
        }

        private float GetCpuTemp()
        {
            try
            {
                _hardwareInfo.RefreshCPUList(includePercentProcessorTime: false);
                var cpu = _hardwareInfo.CpuList.FirstOrDefault();
                // Hardware.Info returns temp in Celsius
                return cpu?.CpuCoreTemperatureList.FirstOrDefault() ?? -1f;
            }
            catch
            {
                return -1f; // unavailable — no sensor or no admin access
            }
        }

        // ─── RAM ──────────────────────────────────────────────────────────────

        private (long used, long available, float percent) GetRamMetrics()
        {
            float availableBytes = PerfCounterPool.RamAvailableBytes();
            long available = (long)availableBytes;
            long used = _ramTotalBytes - available;
            float percent = _ramTotalBytes > 0
                                   ? (used / (float)_ramTotalBytes) * 100f
                                   : 0f;

            return (used, available, percent);
        }

        // ─── GPU ──────────────────────────────────────────────────────────────

        private (float percent, float temp, long vramUsed) GetGpuMetrics()
        {
            try
            {
                _hardwareInfo.RefreshVideoControllerList();
                var gpu = _hardwareInfo.VideoControllerList.FirstOrDefault();
                if (gpu == null) return (-1f, -1f, 0);

                // Hardware.Info provides CurrentUsage and AdapterRAM
                float percent = gpu.CurrentUsage;
                long vramUsed = gpu.AdapterRAM > 0
                                 ? (long)(gpu.AdapterRAM * (percent / 100f))
                                 : 0;

                float temp = GetGpuTemp();
                return (percent, temp, vramUsed);
            }
            catch
            {
                return (-1f, -1f, 0);
            }
        }

        private float GetGpuTemp()
        {
            try
            {
                // Query WMI for GPU temperature — works on most NVIDIA/AMD setups
                using var searcher = new ManagementObjectSearcher(
                    @"root\OpenHardwareMonitor",
                    "SELECT Value FROM Sensor WHERE SensorType='Temperature' AND Name LIKE '%GPU%'");

                foreach (ManagementObject obj in searcher.Get())
                    return Convert.ToSingle(obj["Value"]);

                return -1f;
            }
            catch
            {
                return -1f;
            }
        }

        // ─── Disk ─────────────────────────────────────────────────────────────

        private (float readMBps, float writeMBps, float activePercent) GetDiskMetrics()
        {
            float readBytes = PerfCounterPool.DiskReadBytesPerSec();
            float writeBytes = PerfCounterPool.DiskWriteBytesPerSec();
            float active = PerfCounterPool.DiskActivePercent();

            const float MB = 1024f * 1024f;
            return (readBytes / MB, writeBytes / MB, active);
        }

        // ─── Network ──────────────────────────────────────────────────────────

        private (float upMBps, float downMBps) GetNetworkMetrics()
        {
            if (_networkAdapter is null) return (0f, 0f);

            float sentBytes = PerfCounterPool.NetworkSentBytesPerSec(_networkAdapter);
            float receivedBytes = PerfCounterPool.NetworkReceivedBytesPerSec(_networkAdapter);

            const float MB = 1024f * 1024f;
            return (sentBytes / MB, receivedBytes / MB);
        }

        // ─── Battery ──────────────────────────────────────────────────────────

        private (bool hasBattery, float percent, bool isCharging) GetBatteryMetrics()
        {
            try
            {
                var status = SystemInformation.PowerStatus;
                bool hasBattery = status.BatteryChargeStatus != BatteryChargeStatus.NoSystemBattery
                               && status.BatteryChargeStatus != BatteryChargeStatus.Unknown;

                if (!hasBattery) return (false, -1f, false);

                float percent = status.BatteryLifePercent * 100f;
                bool charging = status.PowerLineStatus == PowerLineStatus.Online;
                return (true, percent, charging);
            }
            catch
            {
                return (false, -1f, false);
            }
        }

        // ─── Slow Data (WMI — cached) ─────────────────────────────────────────

        /// <summary>
        /// Fetches data that changes rarely — GPU name, core counts, RAM total.
        /// Called once at startup and then every 10 seconds via GetSnapshot().
        /// </summary>
        private void RefreshSlowData()
        {
            try
            {
                RefreshCpuInfo();
                RefreshRamTotal();
                RefreshGpuInfo();
                _lastSlowRefresh = DateTime.Now;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SystemInfo slow refresh failed: {ex.Message}");
            }
        }

        private void RefreshCpuInfo()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed " +
                    "FROM Win32_Processor");

                foreach (ManagementObject obj in searcher.Get())
                {
                    _cpuCoreCount = Convert.ToInt32(obj["NumberOfCores"]);
                    _cpuLogicalCount = Convert.ToInt32(obj["NumberOfLogicalProcessors"]);
                    _cpuBaseSpeedGHz = Convert.ToSingle(obj["MaxClockSpeed"]) / 1000f;
                    break; // only need first CPU
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"CPU WMI query failed: {ex.Message}");
                _cpuCoreCount = Environment.ProcessorCount;
                _cpuLogicalCount = Environment.ProcessorCount;
            }
        }

        private void RefreshRamTotal()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT TotalPhysicalMemory FROM Win32_ComputerSystem");

                foreach (ManagementObject obj in searcher.Get())
                {
                    _ramTotalBytes = Convert.ToInt64(obj["TotalPhysicalMemory"]);
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"RAM WMI query failed: {ex.Message}");
            }
        }

        private void RefreshGpuInfo()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, AdapterRAM FROM Win32_VideoController");

                foreach (ManagementObject obj in searcher.Get())
                {
                    _gpuName = obj["Name"]?.ToString() ?? "Unknown GPU";
                    _gpuVramTotal = Convert.ToInt64(obj["AdapterRAM"]);
                    break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"GPU WMI query failed: {ex.Message}");
                _gpuName = "Unavailable";
            }
        }

        // ─── Misc Helpers ─────────────────────────────────────────────────────

        private static TimeSpan GetSystemUptime()
        {
            try
            {
                // GetTickCount64 returns ms since last boot — no WMI needed
                return TimeSpan.FromMilliseconds(GetTickCount64());
            }
            catch
            {
                return TimeSpan.Zero;
            }
        }

        private static int GetTotalThreads()
        {
            try
            {
                return System.Diagnostics.Process.GetProcesses()
                             .Sum(p => { try { return p.Threads.Count; } catch { return 0; } });
            }
            catch { return 0; }
        }

        private static float EstimateTotalWatts(float cpuPercent, float gpuPercent)
        {
            // Rough heuristic — replace with RAPL readings if hardware supports it
            float cpuWatts = cpuPercent * 0.65f;   // ~65W TDP at 100%
            float gpuWatts = gpuPercent < 0 ? 0 : gpuPercent * 0.80f; // ~80W TDP
            float baseWatts = 15f;                  // idle system draw
            return baseWatts + cpuWatts + gpuWatts;
        }

        // ─── P/Invoke ─────────────────────────────────────────────────────────

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();

        // ─── IDisposable ──────────────────────────────────────────────────────

        public void Dispose()
        {
            PerfCounterPool.DisposeAll();
            Logger.Info("SystemInfo disposed.");
        }
    }
}