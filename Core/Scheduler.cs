using System;
using System.Collections.Generic;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Evaluates alert rules against live metrics and triggers power mode switches.
    /// Replaces scheduler.py from the Python prototype.
    ///
    /// Called by UpdaterService on every medium tick (3s).
    /// Does not poll or run its own timer — purely reactive.
    ///
    /// Responsibilities:
    ///   1. Check each AlertConfig rule against the current SystemMetrics snapshot
    ///   2. Track consecutive-seconds counters to enforce SustainSeconds
    ///   3. Fire OnAlertTriggered when a rule crosses its threshold
    ///   4. Call ModeManager.SetMode() when AutoSwitchMode is enabled
    /// </summary>
    public class Scheduler
    {
        private readonly ModeManager _modeManager;
        private readonly IReadOnlyList<AlertConfig> _alerts;

        public Scheduler(ModeManager modeManager, IReadOnlyList<AlertConfig> alerts)
        {
            _modeManager = modeManager;
            _alerts = alerts;
        }

        // ─── Events ───────────────────────────────────────────────────────────

        /// <summary>
        /// Fired when an alert rule transitions from inactive → active.
        /// UI subscribes to show toast notifications.
        /// </summary>
        public event EventHandler<AlertFiredEventArgs>? OnAlertTriggered;

        /// <summary>
        /// Fired when an alert rule transitions from active → inactive (resolved).
        /// </summary>
        public event EventHandler<AlertConfig>? OnAlertResolved;

        // ─── Public: Evaluate ─────────────────────────────────────────────────

        /// <summary>
        /// Evaluates all enabled alert rules against the provided snapshot.
        /// Call this on every medium tick from UpdaterService.
        /// </summary>
        public void Evaluate(SystemMetrics metrics)
        {
            foreach (var alert in _alerts)
            {
                if (!alert.IsEnabled) continue;

                float current = GetMetricValue(alert.Metric, metrics);
                bool exceeded = current >= alert.Threshold;

                if (exceeded)
                {
                    alert.ConsecutiveSeconds++;

                    // Fire only when SustainSeconds threshold is reached
                    if (alert.ConsecutiveSeconds >= alert.SustainSeconds
                        && !alert.IsCurrentlyFired)
                    {
                        TriggerAlert(alert, current, metrics);
                    }
                }
                else
                {
                    // Threshold no longer exceeded — resolve if was active
                    if (alert.IsCurrentlyFired)
                    {
                        alert.IsCurrentlyFired = false;
                        alert.ConsecutiveSeconds = 0;
                        OnAlertResolved?.Invoke(this, alert);
                        Logger.Info($"Alert resolved: {alert.Label}");
                    }
                    else
                    {
                        // Reset counter — must be sustained consecutively
                        alert.ConsecutiveSeconds = 0;
                    }
                }
            }
        }

        // ─── Private: Trigger ─────────────────────────────────────────────────

        private void TriggerAlert(AlertConfig alert, float currentValue, SystemMetrics metrics)
        {
            alert.IsCurrentlyFired = true;
            alert.LastFiredAt = DateTime.UtcNow;

            Logger.Action(
                $"Alert fired: {alert.Label} = {currentValue:F1} " +
                $"(threshold: {alert.Threshold}) severity: {alert.Severity}");

            // Auto power mode switch if configured
            if (alert.AutoSwitchMode
                && _modeManager.CurrentMode != alert.SwitchToMode)
            {
                _modeManager.SetMode(alert.SwitchToMode);
                Logger.Action(
                    $"Auto-switched to {alert.SwitchToMode} mode " +
                    $"triggered by {alert.Label} alert");
            }

            OnAlertTriggered?.Invoke(this, new AlertFiredEventArgs
            {
                Alert = alert,
                CurrentValue = currentValue,
                Metrics = metrics,
            });
        }

        // ─── Private: Metric Extraction ───────────────────────────────────────

        private static float GetMetricValue(AlertMetric metric, SystemMetrics m) => metric switch
        {
            AlertMetric.Cpu => m.CpuPercent,
            AlertMetric.Ram => m.RamPercent,
            AlertMetric.Gpu => m.GpuPercent,
            AlertMetric.CpuTemp => m.CpuTempCelsius,
            AlertMetric.GpuTemp => m.GpuTempCelsius,
            AlertMetric.Disk => m.DiskPercent,
            AlertMetric.Network => m.NetworkTotalMBps,
            AlertMetric.Battery => m.BatteryPercent,
            _ => 0f,
        };
    }

    // ─── Event Args ───────────────────────────────────────────────────────────

    public class AlertFiredEventArgs : EventArgs
    {
        public AlertConfig Alert { get; init; } = null!;
        public float CurrentValue { get; init; }
        public SystemMetrics Metrics { get; init; } = null!;
    }
}