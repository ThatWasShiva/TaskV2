using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Switches Windows power plans to implement Performance / Balanced / Efficiency modes.
    /// Replaces mode_manager.py from the Python prototype.
    ///
    /// Uses powercfg.exe to switch plans — more reliable than WMI for this operation.
    /// Power plan GUIDs are defined in Constants.PowerPlanGuids.
    /// </summary>
    public class ModeManager
    {
        private PowerMode _currentMode = PowerMode.Balanced;

        public PowerMode CurrentMode => _currentMode;

        // ─── Public: Switch Mode ──────────────────────────────────────────────

        /// <summary>
        /// Switches the Windows power plan to match the requested mode.
        /// Returns true if the switch succeeded.
        /// </summary>
        public bool SetMode(PowerMode mode)
        {
            if (mode == _currentMode)
            {
                Logger.Info($"Power mode already set to {mode} — no change.");
                return true;
            }

            var guid = mode switch
            {
                PowerMode.Performance => Constants.PowerPlanGuids.Performance,
                PowerMode.Balanced => Constants.PowerPlanGuids.Balanced,
                PowerMode.Efficiency => Constants.PowerPlanGuids.Efficiency,
                _ => Constants.PowerPlanGuids.Balanced,
            };

            var success = ApplyPowerPlan(guid);

            if (success)
            {
                var previous = _currentMode;
                _currentMode = mode;
                Logger.Action($"Power mode switched: {previous} → {mode}");
                OnModeChanged?.Invoke(this, mode);
            }
            else
            {
                Logger.Action($"Power mode switch failed: {_currentMode} → {mode}", success: false);
            }

            return success;
        }

        /// <summary>
        /// Toggles between Performance and Efficiency.
        /// If currently Balanced, switches to Performance.
        /// </summary>
        public bool Toggle()
        {
            var next = _currentMode == PowerMode.Efficiency
                       ? PowerMode.Performance
                       : PowerMode.Efficiency;
            return SetMode(next);
        }

        // ─── Public: Query Current Plan ───────────────────────────────────────

        /// <summary>
        /// Reads the currently active Windows power plan from the OS.
        /// Syncs internal state to match — call at startup.
        /// </summary>
        public PowerMode DetectCurrentMode()
        {
            try
            {
                var output = RunPowercfg("/getactivescheme");
                if (output.Contains(Constants.PowerPlanGuids.Performance))
                    _currentMode = PowerMode.Performance;
                else if (output.Contains(Constants.PowerPlanGuids.Efficiency))
                    _currentMode = PowerMode.Efficiency;
                else
                    _currentMode = PowerMode.Balanced;

                Logger.Info($"Detected power mode: {_currentMode}");
                return _currentMode;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Power mode detection failed: {ex.Message}");
                return PowerMode.Balanced;
            }
        }

        // ─── Event ────────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when the power mode changes successfully.
        /// The Scheduler and UI subscribe to this to react to mode changes.
        /// </summary>
        public event EventHandler<PowerMode>? OnModeChanged;

        // ─── Private: powercfg.exe ────────────────────────────────────────────

        /// <summary>
        /// Applies a power plan by GUID using powercfg.exe.
        /// powercfg is the most reliable way to do this — WMI alternatives
        /// are fragile and require specific WMI namespaces not always present.
        /// </summary>
        private static bool ApplyPowerPlan(string guid)
        {
            try
            {
                var result = RunPowercfg($"/setactive {guid}");
                // powercfg returns empty string on success
                return !result.ToLower().Contains("error");
            }
            catch (Exception ex)
            {
                Logger.Error($"powercfg failed: {ex.Message}");
                return false;
            }
        }

        private static string RunPowercfg(string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powercfg.exe",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi)
                             ?? throw new InvalidOperationException("Failed to start powercfg.exe");

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000); // 3s timeout
            return output;
        }
    }
}