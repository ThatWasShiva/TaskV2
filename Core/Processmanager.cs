using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using TaskManager.Models;
using TaskManager.Utils;

namespace TaskManager.Core
{
    /// <summary>
    /// Manages the running process list — listing, killing, suspending, priority changes.
    /// Replaces process_manager.py from the Python prototype.
    ///
    /// Design rules:
    ///   - Never kills a protected process (checked via Constants.ProtectedProcessNames)
    ///   - All actions logged via Logger.Action()
    ///   - Unsafe process access wrapped in try/catch — system processes deny reads
    ///   - Returns result objects instead of throwing — caller decides how to handle
    /// </summary>
    public class ProcessManager
    {
        // ─── Public: List ─────────────────────────────────────────────────────

        /// <summary>
        /// Returns a snapshot of all currently running processes.
        /// Filters out the Task Manager process itself.
        /// </summary>
        public IReadOnlyList<ProcessInfo> GetAll()
        {
            var result = new List<ProcessInfo>();
            var all = Process.GetProcesses();

            foreach (var p in all)
            {
                try
                {
                    // Skip our own process
                    if (p.Id == Environment.ProcessId) continue;

                    var info = BuildProcessInfo(p);
                    result.Add(info);
                }
                catch
                {
                    // Some system processes throw on any property access — skip them
                }
                finally
                {
                    p.Dispose();
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a single ProcessInfo by PID.
        /// Returns null if the process no longer exists or access is denied.
        /// </summary>
        public ProcessInfo? GetByPid(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return BuildProcessInfo(p);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Refreshes only the resource-usage fields of existing ProcessInfo objects.
        /// More efficient than calling GetAll() — avoids re-classifying every process.
        /// </summary>
        public void RefreshUsage(IList<ProcessInfo> existing)
        {
            var currentPids = new HashSet<int>(existing.Select(p => p.Pid));

            foreach (var info in existing)
            {
                try
                {
                    using var p = Process.GetProcessById(info.Pid);
                    UpdateUsageFields(info, p);
                }
                catch
                {
                    // Process exited between ticks — caller will remove it via diff
                }
            }
        }

        // ─── Public: Kill ─────────────────────────────────────────────────────

        /// <summary>
        /// Terminates a process by PID.
        /// Returns a ProcessActionResult indicating success or the reason for failure.
        /// </summary>
        public ProcessActionResult Kill(int pid)
        {
            if (IsProtected(pid, out var name))
            {
                Logger.Action($"Kill blocked — protected process: {name} PID:{pid}", success: false);
                return ProcessActionResult.Fail($"{name} is a protected system process and cannot be ended.");
            }

            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: false);
                Logger.Action($"Killed PID:{pid} ({p.ProcessName})");
                return ProcessActionResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Action($"Kill failed PID:{pid} — {ex.Message}", success: false);
                return ProcessActionResult.Fail($"Could not end process: {ex.Message}");
            }
        }

        /// <summary>
        /// Terminates a process and all its children.
        /// Use with caution — child processes may include important services.
        /// </summary>
        public ProcessActionResult KillTree(int pid)
        {
            if (IsProtected(pid, out var name))
                return ProcessActionResult.Fail($"{name} is protected.");

            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                Logger.Action($"Killed process tree rooted at PID:{pid} ({p.ProcessName})");
                return ProcessActionResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Action($"KillTree failed PID:{pid} — {ex.Message}", success: false);
                return ProcessActionResult.Fail(ex.Message);
            }
        }

        // ─── Public: Suspend / Resume ─────────────────────────────────────────

        /// <summary>
        /// Suspends all threads of a process (pauses execution).
        /// Windows has no single "suspend process" API — we suspend each thread.
        /// </summary>
        public ProcessActionResult Suspend(int pid)
        {
            if (IsProtected(pid, out var name))
                return ProcessActionResult.Fail($"{name} is a protected process.");

            try
            {
                using var p = Process.GetProcessById(pid);
                foreach (ProcessThread thread in p.Threads)
                {
                    var handle = OpenThread(ThreadAccess.SuspendResume, false, (uint)thread.Id);
                    if (handle == IntPtr.Zero) continue;
                    try { SuspendThread(handle); }
                    finally { CloseHandle(handle); }
                }
                Logger.Action($"Suspended PID:{pid} ({p.ProcessName})");
                return ProcessActionResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Action($"Suspend failed PID:{pid} — {ex.Message}", success: false);
                return ProcessActionResult.Fail(ex.Message);
            }
        }

        /// <summary>
        /// Resumes all threads of a previously suspended process.
        /// </summary>
        public ProcessActionResult Resume(int pid)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                foreach (ProcessThread thread in p.Threads)
                {
                    var handle = OpenThread(ThreadAccess.SuspendResume, false, (uint)thread.Id);
                    if (handle == IntPtr.Zero) continue;
                    try { ResumeThread(handle); }
                    finally { CloseHandle(handle); }
                }
                Logger.Action($"Resumed PID:{pid} ({p.ProcessName})");
                return ProcessActionResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Action($"Resume failed PID:{pid} — {ex.Message}", success: false);
                return ProcessActionResult.Fail(ex.Message);
            }
        }

        // ─── Public: Priority ─────────────────────────────────────────────────

        /// <summary>
        /// Changes the priority class of a process.
        /// Realtime priority requires admin — blocked unless elevated.
        /// </summary>
        public ProcessActionResult SetPriority(int pid, ProcessPriorityClass priority)
        {
            if (IsProtected(pid, out var name))
                return ProcessActionResult.Fail($"{name} is a protected process.");

            if (priority == ProcessPriorityClass.RealTime && !IsAdmin())
                return ProcessActionResult.Fail("Realtime priority requires administrator privileges.");

            try
            {
                using var p = Process.GetProcessById(pid);
                p.PriorityClass = priority;
                Logger.Action($"Priority changed PID:{pid} ({p.ProcessName}) → {priority}");
                return ProcessActionResult.Ok();
            }
            catch (Exception ex)
            {
                Logger.Action($"Priority change failed PID:{pid} — {ex.Message}", success: false);
                return ProcessActionResult.Fail(ex.Message);
            }
        }

        // ─── Private: Build ProcessInfo ───────────────────────────────────────

        private static ProcessInfo BuildProcessInfo(Process p)
        {
            var name = p.ProcessName;
            var isProtected = Constants.ProtectedProcessNames.Contains(name);
            var type = Helpers.ClassifyProcess(p);

            return new ProcessInfo
            {
                Pid = p.Id,
                Name = name + ".exe",
                DisplayName = ToDisplayName(name),
                Type = type,
                Status = ProcessStatus.Running,
                IsProtected = isProtected,
                SessionId = p.SessionId,
                StartTime = Helpers.TryGetStartTime(p),
                ThreadCount = Helpers.TryGetThreadCount(p),
                Priority = (int)p.PriorityClass,
                PriorityLabel = Constants.PriorityLabels.TryGetValue(
                                    (int)p.PriorityClass, out var lbl) ? lbl : "Normal",
                RamBytes = p.WorkingSet64,
                LastUpdated = DateTime.UtcNow,
                // CPU%, Disk, Network filled in by PowerEstimator / UpdaterService
                // ExecutablePath, CommandLine, Owner = lazy — not loaded here
            };
        }

        /// <summary>
        /// Updates only the live-changing fields of an existing ProcessInfo.
        /// Avoids full object replacement on every poll tick.
        /// </summary>
        private static void UpdateUsageFields(ProcessInfo info, Process p)
        {
            info.RamBytes = p.WorkingSet64;
            info.ThreadCount = Helpers.TryGetThreadCount(p);
            info.LastUpdated = DateTime.UtcNow;
            // CPU% and network updated separately by PerfCounterPool
        }

        // ─── Private: Guards ──────────────────────────────────────────────────

        private static bool IsProtected(int pid, out string name)
        {
            name = string.Empty;
            try
            {
                using var p = Process.GetProcessById(pid);
                name = p.ProcessName;
                return Constants.ProtectedProcessNames.Contains(name);
            }
            catch { return false; }
        }

        private static bool IsAdmin()
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }

        // ─── Private: Display Name ────────────────────────────────────────────

        private static string ToDisplayName(string processName)
        {
            // Try to get the friendly name from the process description via FileVersionInfo
            // Falls back to title-cased process name
            try
            {
                using var p = Process.GetProcessesByName(processName).FirstOrDefault();
                if (p?.MainModule?.FileName is string path)
                {
                    var info = FileVersionInfo.GetVersionInfo(path);
                    if (!string.IsNullOrWhiteSpace(info.FileDescription))
                        return info.FileDescription;
                }
            }
            catch { }

            // Fallback: "chrome" → "Chrome", "visual studio code" → "Visual Studio Code"
            return System.Globalization.CultureInfo.CurrentCulture
                         .TextInfo.ToTitleCase(processName.Replace('-', ' ').Replace('_', ' '));
        }

        // ─── P/Invoke: Thread Suspend/Resume ──────────────────────────────────
        // Windows has no managed API for suspending all threads of a process.

        [Flags]
        private enum ThreadAccess : int
        {
            SuspendResume = 0x0002,
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr OpenThread(ThreadAccess access, bool inherit, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint SuspendThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        private static extern int ResumeThread(IntPtr hThread);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);
    }

    // ─── Result Type ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returned by all ProcessManager actions instead of throwing exceptions.
    /// The UI reads Success and Message to decide what to show the user.
    /// </summary>
    public class ProcessActionResult
    {
        public bool Success { get; private init; }
        public string Message { get; private init; } = string.Empty;

        public static ProcessActionResult Ok()
            => new() { Success = true, Message = "Success" };

        public static ProcessActionResult Fail(string reason)
            => new() { Success = false, Message = reason };
    }
}