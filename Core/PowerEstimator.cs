using System.Collections.Generic;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Estimates per-process power impact using a heuristic scoring model.
    /// Replaces power_estimator.py from the Python prototype.
    ///
    /// Cannot measure true per-process wattage without hardware sensors,
    /// so we compute a weighted score from CPU, disk, and network usage
    /// and map it to a Low / Medium / High / VeryHigh impact level.
    ///
    /// The formula and weights are defined in Helpers.CalcImpactScore()
    /// so they can be tuned in one place across the whole app.
    /// </summary>
    public class PowerEstimator
    {
        // ─── Public: Single Process ───────────────────────────────────────────

        /// <summary>
        /// Computes and sets the PowerImpact and EstimatedWatts fields
        /// on a single ProcessInfo in place.
        /// </summary>
        public void Estimate(ProcessInfo process)
        {
            var score = Helpers.CalcImpactScore(
                             process.CpuPercent,
                             process.DiskMBps,
                             process.NetworkMBps);

            process.PowerImpact = Helpers.ScoreToImpact(score);
            process.EstimatedWatts = EstimateWatts(process.CpuPercent, score);
        }

        /// <summary>
        /// Computes and sets PowerImpact and EstimatedWatts on all processes in a list.
        /// Call this after updating CPU/disk/network usage fields.
        /// </summary>
        public void EstimateAll(IList<ProcessInfo> processes)
        {
            foreach (var p in processes)
                Estimate(p);
        }

        // ─── Public: System-wide Summary ─────────────────────────────────────

        /// <summary>
        /// Returns the overall system power impact based on the top consumers.
        /// Used for the battery impact badge in the UI header.
        /// </summary>
        public PowerImpact GetSystemImpact(IList<ProcessInfo> processes)
        {
            if (processes.Count == 0) return PowerImpact.Low;

            // Use the top 3 processes' combined score
            float topScore = 0f;
            int count = 0;

            foreach (var p in processes)
            {
                if (count++ >= 3) break;
                topScore += Helpers.CalcImpactScore(p.CpuPercent, p.DiskMBps, p.NetworkMBps);
            }

            return Helpers.ScoreToImpact(topScore / 3f);
        }

        /// <summary>
        /// Returns the estimated total wattage drawn by all processes combined.
        /// This is additive but capped — real power is not perfectly additive.
        /// </summary>
        public float GetTotalEstimatedWatts(IList<ProcessInfo> processes)
        {
            float total = 0f;
            foreach (var p in processes)
                total += p.EstimatedWatts;

            // Cap at a realistic system maximum to prevent runaway estimates
            return System.Math.Min(total, 250f);
        }

        // ─── Private: Watt Estimate ───────────────────────────────────────────

        /// <summary>
        /// Rough per-process watt estimate.
        /// CPU contribution is the dominant factor — disk and network are minor.
        /// These weights are intentionally conservative — real usage varies widely.
        /// </summary>
        private static float EstimateWatts(float cpuPercent, float impactScore)
        {
            // Assume a 65W TDP CPU — per-process share proportional to CPU usage
            const float CpuTdp = 65f;
            float cpuShare = (cpuPercent / 100f) * CpuTdp;

            // Small overhead for disk and network activity
            float overhead = impactScore * 0.05f;

            return cpuShare + overhead;
        }
    }
}