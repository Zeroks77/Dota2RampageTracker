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

        public static void Info(string msg) => Write("INFO", msg, ConsoleColor.White);
        public static void Warn(string msg) => Write("WARN", msg, ConsoleColor.Yellow);
        public static void Error(string msg) => Write("ERROR", msg, ConsoleColor.Red);
        public static void Success(string msg) => Write("SUCCESS", msg, ConsoleColor.Green);
        public static void Debug(string msg) => Write("DEBUG", msg, ConsoleColor.Gray);

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
    }
}
