using System;

namespace RampageTracker.Core
{
    public static class Logger
    {
        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        public static void Error(string msg) => Write("ERROR", msg);

        private static void Write(string level, string msg)
        {
            Console.WriteLine($"[{DateTime.UtcNow:HH:mm:ss}] {level} {msg}");
        }
    }
}
