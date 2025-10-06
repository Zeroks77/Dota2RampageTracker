using System;
using System.Threading;

namespace RampageTracker.Core
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static int _requestCount = 0;
        private static int _rampageCount = 0;
        private static int _errorCount = 0;
        private static DateTime _lastStatsLog = DateTime.UtcNow;
        private static int _lastRequestCount = 0;
        private static System.IO.StreamWriter? _logWriter;
        private static System.IO.StreamWriter? _latestWriter;
        private static string? _logFilePath;
        private static string? _latestFilePath;

        public static void Info(string msg) => Write("INFO", msg, ConsoleColor.White);
        public static void Warn(string msg) => Write("WARN", msg, ConsoleColor.Yellow);
        public static void Error(string msg) => Write("ERROR", msg, ConsoleColor.Red);
        public static void Success(string msg) => Write("SUCCESS", msg, ConsoleColor.Green);
        public static void Debug(string msg) => Write("DEBUG", msg, ConsoleColor.Gray);

        public static void Initialize(string repoRoot, string? fileNameSuffix = null)
        {
            lock (_lock)
            {
                try
                {
                    // Close previous writers if re-initialized
                    try { _logWriter?.Dispose(); } catch { }
                    try { _latestWriter?.Dispose(); } catch { }
                    _logWriter = null; _latestWriter = null;

                    var logsDir = System.IO.Path.Combine(repoRoot, "logs");
                    System.IO.Directory.CreateDirectory(logsDir);
                    var ts = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
                    var suffix = string.IsNullOrWhiteSpace(fileNameSuffix) ? string.Empty : ("_" + Sanitize(fileNameSuffix));
                    _logFilePath = System.IO.Path.Combine(logsDir, $"run_{ts}_UTC{suffix}.log");
                    _latestFilePath = System.IO.Path.Combine(logsDir, "latest.log");

                    var fs = new System.IO.FileStream(_logFilePath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
                    _logWriter = new System.IO.StreamWriter(fs, new System.Text.UTF8Encoding(false)) { AutoFlush = true };

                    var lfs = new System.IO.FileStream(_latestFilePath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
                    _latestWriter = new System.IO.StreamWriter(lfs, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
                }
                catch (Exception ex)
                {
                    // Fallback: just note that file logging failed, but keep console logging
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"[LOGGER] File logging initialization failed: {ex.Message}");
                    Console.ResetColor();
                }
            }
        }

        public static void Close()
        {
            lock (_lock)
            {
                try { _logWriter?.Flush(); } catch { }
                try { _latestWriter?.Flush(); } catch { }
                try { _logWriter?.Dispose(); } catch { }
                try { _latestWriter?.Dispose(); } catch { }
                _logWriter = null; _latestWriter = null;
            }
        }

        // Spezielle Logging-Methoden für bessere Übersicht
        public static void LogApiRequest(string endpoint, long? playerId = null, long? matchId = null)
        {
            var currentCount = Interlocked.Increment(ref _requestCount);
            var details = "";
            if (playerId.HasValue) details += $" Player:{playerId}";
            if (matchId.HasValue) details += $" Match:{matchId}";
            Debug($"API [{currentCount:D6}] {endpoint}{details}");
            
            // Log API rate every minute
            var now = DateTime.UtcNow;
            if ((now - _lastStatsLog).TotalMinutes >= 1.0)
            {
                lock (_lock)
                {
                    if ((DateTime.UtcNow - _lastStatsLog).TotalMinutes >= 1.0)
                    {
                        var requestsThisMinute = currentCount - _lastRequestCount;
                        var actualRate = requestsThisMinute / (now - _lastStatsLog).TotalMinutes;
                        var target = RampageTracker.Core.RateLimiter.GetTargetPerMinute();
                        Info($"📊 API Rate: {requestsThisMinute} requests in last {(now - _lastStatsLog).TotalMinutes:F1}min = {actualRate:F1} req/min (Target: {target} req/min)");
                        _lastStatsLog = now;
                        _lastRequestCount = currentCount;
                    }
                }
            }
        }

        public static void LogRampageFound(long playerId, long matchId, string heroName = "Unknown")
        {
            Interlocked.Increment(ref _rampageCount);
            Success($"🎯 RAMPAGE #{_rampageCount:D4} - Player:{playerId} Match:{matchId} Hero:{heroName} [SAVING NOW]");
        }

        public static void LogMatchEvaluation(long playerId, long matchId, string? heroName, int multiKill5Count, bool isRampage)
        {
            var hero = string.IsNullOrWhiteSpace(heroName) ? "Unknown" : heroName;
            var verdict = isRampage ? "YES" : "no";
            Debug($"[eval] Player:{playerId} Match:{matchId} Hero:{hero} multi_kills[5]={multiKill5Count} -> Rampage: {verdict}");
        }

        public static void LogMatchRampageSummary(long matchId, IEnumerable<(long? AccountId, string? HeroName, int Count)> rampagers)
        {
            try
            {
                var list = rampagers?.ToList() ?? new List<(long? AccountId, string? HeroName, int Count)>();
                if (list.Count == 0)
                {
                    Info($"[summary] Match {matchId}: no rampage detected");
                    return;
                }
                var parts = list.Select(x => $"{(x.AccountId?.ToString() ?? "unknown")}{(string.IsNullOrWhiteSpace(x.HeroName) ? "" : $" ({x.HeroName})")} x{x.Count}");
                Info($"[summary] Match {matchId}: rampage by {string.Join(", ", parts)}");
            }
            catch (Exception ex)
            {
                Debug($"[summary] Failed to log rampage summary for match {matchId}: {ex.Message}");
            }
        }

        public static void LogPlayerProgress(long playerId, int processed, int total, int rampagesFound)
        {
            var progress = total > 0 ? (processed * 100.0 / total) : 0;
            Info($"👤 Player {playerId}: {processed}/{total} matches ({progress:F1}%) - {rampagesFound} rampages");
        }

        public static void LogError(string operation, Exception ex, long? playerId = null, long? matchId = null)
        {
            Interlocked.Increment(ref _errorCount);
            var details = "";
            if (playerId.HasValue) details += $" Player:{playerId}";
            if (matchId.HasValue) details += $" Match:{matchId}";
            Error($"❌ [{_errorCount:D4}] {operation}{details} - {ex.Message}");
            if (ex.InnerException != null)
            {
                Error($"    ↳ Inner: {ex.InnerException.Message}");
            }
        }

        public static void LogStatistics()
        {
            Info($"📊 Statistics - API Requests: {_requestCount}, Rampages: {_rampageCount}, Errors: {_errorCount}");
        }

        public static void LogRateLimit(double remainingSeconds)
        {
            Warn($"⏱️  Rate limit hit - waiting {remainingSeconds:F0}s to reset...");
        }

        private static void Write(string level, string msg, ConsoleColor color)
        {
            lock (_lock)
            {
                var timestamp = DateTime.UtcNow.ToString("HH:mm:ss.fff");
                var threadId = Thread.CurrentThread.ManagedThreadId.ToString("D3");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[{timestamp}]");
                
                Console.ForegroundColor = GetLevelColor(level);
                Console.Write($"[{level}]");
                
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"[T{threadId}] ");
                
                Console.ForegroundColor = color;
                Console.WriteLine(msg);
                
                Console.ResetColor();

                // Also write to log files (without ANSI colors)
                try
                {
                    var line = $"[{timestamp}][{level}][T{threadId}] {msg}";
                    _logWriter?.WriteLine(line);
                    _latestWriter?.WriteLine(line);
                }
                catch { /* ignore file logging errors */ }
            }
        }

        private static ConsoleColor GetLevelColor(string level) => level switch
        {
            "INFO" => ConsoleColor.Cyan,
            "WARN" => ConsoleColor.Yellow,
            "ERROR" => ConsoleColor.Red,
            "SUCCESS" => ConsoleColor.Green,
            "DEBUG" => ConsoleColor.DarkGray,
            _ => ConsoleColor.White
        };

        private static string Sanitize(string input)
        {
            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
            {
                input = input.Replace(ch, '_');
            }
            return input;
        }
    }
}
