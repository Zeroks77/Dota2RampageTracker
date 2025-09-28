using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace OpenDotaRampage.Helpers
{
    public static class Logger
    {
        public enum LogLevel
        {
            Debug = 0,
            Info = 1,
            Warn = 2,
            Error = 3
        }

        private static readonly object _lock = new object();
        private static string? _logFilePath;
        private static bool _json;
        private static LogLevel _level = LogLevel.Info;
        private static string _runId = Guid.NewGuid().ToString("N");

        public static void Init(string? logFilePath = null, LogLevel level = LogLevel.Info, bool json = false, string? runId = null)
        {
            _logFilePath = logFilePath;
            _level = level;
            _json = json;
            if (!string.IsNullOrWhiteSpace(runId)) _runId = runId!;
            if (!string.IsNullOrWhiteSpace(_logFilePath))
            {
                try
                {
                    var dir = Path.GetDirectoryName(_logFilePath!);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                }
                catch { /* best effort */ }
            }
        }

        public static void Debug(string message, string category = "app", Dictionary<string, object?>? ctx = null)
            => Log(LogLevel.Debug, message, category, ctx);
        public static void Info(string message, string category = "app", Dictionary<string, object?>? ctx = null)
            => Log(LogLevel.Info, message, category, ctx);
        public static void Warn(string message, string category = "app", Dictionary<string, object?>? ctx = null)
            => Log(LogLevel.Warn, message, category, ctx);
        public static void Error(string message, string category = "app", Dictionary<string, object?>? ctx = null)
            => Log(LogLevel.Error, message, category, ctx);

        public static void Log(LogLevel level, string message, string category = "app", Dictionary<string, object?>? ctx = null)
        {
            if (level < _level) return;
            var ts = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            if (_json)
            {
                var obj = new Dictionary<string, object?>
                {
                    ["ts"] = ts,
                    ["level"] = level.ToString().ToLowerInvariant(),
                    ["runId"] = _runId,
                    ["cat"] = category,
                    ["msg"] = message
                };
                if (ctx != null)
                {
                    foreach (var kv in ctx) obj[kv.Key] = kv.Value;
                }
                var line = JsonConvert.SerializeObject(obj);
                WriteLine(line);
            }
            else
            {
                var sb = new StringBuilder();
                sb.Append('[').Append(ts).Append("] ");
                sb.Append(level.ToString().ToUpperInvariant()).Append(' ');
                sb.Append('(').Append(_runId).Append(") ");
                sb.Append('[').Append(category).Append("] ");
                sb.Append(message);
                if (ctx != null && ctx.Count > 0)
                {
                    sb.Append(" | ");
                    bool first = true;
                    foreach (var kv in ctx)
                    {
                        if (!first) sb.Append(' ');
                        first = false;
                        sb.Append(kv.Key).Append('=').Append(kv.Value);
                    }
                }
                WriteLine(sb.ToString());
            }
        }

        private static void WriteLine(string line)
        {
            lock (_lock)
            {
                try { Console.WriteLine(line); } catch { /* ignore */ }
                if (!string.IsNullOrWhiteSpace(_logFilePath))
                {
                    try { File.AppendAllText(_logFilePath!, line + Environment.NewLine); } catch { /* ignore */ }
                }
            }
        }
    }
}
