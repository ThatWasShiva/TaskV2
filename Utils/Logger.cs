using System;
using System.IO;
using System.Text;
using System.Threading;

namespace TaskManager.Utils
{
    /// <summary>
    /// Lightweight file + debug logger.
    /// Replaces logger.py from the Python prototype.
    ///
    /// Security rules applied:
    ///   - Never logs usernames, passwords, or full file paths
    ///   - Timestamps are always UTC
    ///   - Log files are capped and rotated (see Constants.Logging)
    ///   - Thread-safe via lock
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new();
        private static string? _logPath;
        private static bool _initialised;

        // ─── Public API ───────────────────────────────────────────────────────

        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warn(string message) => Write(LogLevel.Warning, message);
        public static void Error(string message) => Write(LogLevel.Error, message);
        public static void Debug(string message) => Write(LogLevel.Debug, message);

        /// <summary>
        /// Logs a user-initiated action e.g. "Killed PID:1234" or "Switched to Efficiency mode".
        /// These are the audit trail entries — never include sensitive data.
        /// </summary>
        public static void Action(string action, bool success = true)
        {
            var result = success ? "OK" : "FAILED";
            Write(LogLevel.Action, $"[{result}] {action}");
        }

        // ─── Init ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Call once at startup to set the log file path.
        /// If not called, logs go to debug output only.
        /// </summary>
        public static void Initialise(string logDirectory)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(logDirectory);
                    var filename = $"{Constants.Logging.LogFilePrefix}{DateTime.UtcNow:yyyyMMdd}.log";
                    _logPath = Path.Combine(logDirectory, filename);
                    _initialised = true;

                    RotateIfNeeded(logDirectory);
                    Write(LogLevel.Info, "─── Logger initialised ───");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Logger] Init failed: {ex.Message}");
                }
            }
        }

        // ─── Private ──────────────────────────────────────────────────────────

        private static void Write(LogLevel level, string message)
        {
            var line = Format(level, message);

            // Always write to debug output (visible in VS Output window)
            System.Diagnostics.Debug.WriteLine(line);

            if (!_initialised || _logPath is null) return;

            lock (_lock)
            {
                try
                {
                    File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
                }
                catch
                {
                    // Swallow — logging must never crash the app
                }
            }
        }

        private static string Format(LogLevel level, string message)
        {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var tag = level switch
            {
                LogLevel.Info => "INFO ",
                LogLevel.Warning => "WARN ",
                LogLevel.Error => "ERROR",
                LogLevel.Debug => "DEBUG",
                LogLevel.Action => "ACTN ",
                _ => "     "
            };
            var thread = Thread.CurrentThread.ManagedThreadId;
            return $"[{timestamp}] [{tag}] [T{thread:D2}] {message}";
        }

        /// <summary>
        /// Deletes oldest log files when count exceeds MaxLogFiles.
        /// Caps individual file size — starts new file if too large.
        /// </summary>
        private static void RotateIfNeeded(string logDirectory)
        {
            try
            {
                // Cap file size
                if (_logPath is not null && File.Exists(_logPath))
                {
                    var info = new FileInfo(_logPath);
                    if (info.Length > Constants.Logging.MaxLogFileSizeBytes)
                    {
                        var rotated = _logPath.Replace(".log", $"_{DateTime.UtcNow:HHmmss}.log");
                        File.Move(_logPath, rotated);
                    }
                }

                // Prune old files
                var files = Directory.GetFiles(logDirectory,
                    $"{Constants.Logging.LogFilePrefix}*.log");

                if (files.Length > Constants.Logging.MaxLogFiles)
                {
                    Array.Sort(files); // oldest first by name (date-prefixed)
                    for (int i = 0; i < files.Length - Constants.Logging.MaxLogFiles; i++)
                    {
                        try { File.Delete(files[i]); }
                        catch { /* best effort */ }
                    }
                }
            }
            catch { /* rotation failure is non-fatal */ }
        }
    }

    // ─── Supporting Enum ──────────────────────────────────────────────────────

    public enum LogLevel
    {
        Debug,
        Info,
        Warning,
        Error,
        Action
    }
}