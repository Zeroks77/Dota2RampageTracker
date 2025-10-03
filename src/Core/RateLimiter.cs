using System;
using System.Threading;
using System.Threading.Tasks;

namespace RampageTracker.Core
{
    public static class RateLimiter
    {
        private static int _maxPerMinute = 60;
        private static int _count = 0;
        private static DateTime _window = DateTime.UtcNow;
        private static readonly SemaphoreSlim _gate = new(1, 1);
        private static SemaphoreSlim _concurrency = new(16, 16);

        public static void Initialize(bool useApiKey, int? maxConcurrency = null)
        {
            _maxPerMinute = useApiKey ? 120 : 60;
            var target = Math.Max(1, maxConcurrency ?? 16);
            // Replace the concurrency gate at startup
            var old = _concurrency;
            _concurrency = new SemaphoreSlim(target, target);
            try { old?.Dispose(); } catch { }
        }

        public static async Task EnsureRateAsync()
        {
            await _concurrency.WaitAsync();
            await _gate.WaitAsync();
            try
            {
                var now = DateTime.UtcNow;
                if ((now - _window).TotalMinutes >= 1)
                {
                    _window = now;
                    _count = 0;
                }
                _count++;
                if (_count > _maxPerMinute)
                {
                    var secondsLeft = Math.Max(0, 60 - (now - _window).TotalSeconds);
                    var delay = TimeSpan.FromSeconds(secondsLeft);
                    if (delay > TimeSpan.Zero)
                    {
                        // Heartbeat log to prevent long silent periods in CI
                        if (delay.TotalSeconds >= 5)
                        {
                            Logger.Info($"Rate limit reached, sleeping {delay.TotalSeconds:F0}s to reset window...");
                        }
                        await Task.Delay(delay);
                    }
                    _window = DateTime.UtcNow;
                    _count = 0;
                }
            }
            finally
            {
                _gate.Release();
                _concurrency.Release();
            }
        }
    }
}