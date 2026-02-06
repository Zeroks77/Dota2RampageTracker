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
        private static int _maxConcurrency = 16;

        public static void Initialize(bool useApiKey, int? maxConcurrency = null)
        {
            // Keep a safety buffer when using API key to reduce 429s
            _maxPerMinute = useApiKey ? 3000 : 60;
            _maxConcurrency = Math.Max(1, maxConcurrency ?? 16);
            
            // For high concurrency with API key, allow many parallel requests
            var concurrencyLimit = useApiKey ? Math.Min(_maxConcurrency, 256) : Math.Min(_maxConcurrency, 16);
            
            // Replace the concurrency gate at startup
            var old = _concurrency;
            _concurrency = new SemaphoreSlim(concurrencyLimit, concurrencyLimit);
            try { old?.Dispose(); } catch { }
            
            Logger.Success($"RateLimiter initialized: {_maxPerMinute} req/min ({_maxPerMinute/60.0:F1} req/sec ideal), {concurrencyLimit} concurrent slots");
        }

        public static void UpdateApiKeyMode(bool useApiKey)
        {
            var newMax = useApiKey ? 3000 : 60;
            if (_maxPerMinute == newMax) return;
            _maxPerMinute = newMax;
            Logger.Info($"RateLimiter updated: {_maxPerMinute} req/min ({_maxPerMinute/60.0:F1} req/sec ideal)");
        }

        public static async Task EnsureRateAsync()
        {
            // Fast path: Just count requests for monitoring, minimal throttling
            var currentCount = Interlocked.Increment(ref _count);
            var now = DateTime.UtcNow;
            
            // Reset counter every minute (lock-free in most cases)
            if ((now - _window).TotalMinutes >= 1)
            {
                if (await _gate.WaitAsync(10)) // Quick timeout
                {
                    try
                    {
                        if ((DateTime.UtcNow - _window).TotalMinutes >= 1)
                        {
                            _window = DateTime.UtcNow;
                            Interlocked.Exchange(ref _count, 1);
                            currentCount = 1;
                        }
                    }
                    finally
                    {
                        _gate.Release();
                    }
                }
            }
            
            // Throttle when over limit: wait for next window
            var windowAge = (now - _window).TotalSeconds;
            if (windowAge >= 0 && currentCount > _maxPerMinute)
            {
                await _gate.WaitAsync();
                try
                {
                    var now2 = DateTime.UtcNow;
                    var age = (now2 - _window).TotalSeconds;
                    if (age < 60 && _count > _maxPerMinute)
                    {
                        var delayMs = Math.Max(0, (int)((60 - age) * 1000));
                        Logger.Warn($"Rate limit hit: waiting {delayMs}ms to reset window.");
                        await Task.Delay(delayMs);
                        _window = DateTime.UtcNow;
                        Interlocked.Exchange(ref _count, 1);
                    }
                }
                finally
                {
                    _gate.Release();
                }
            }
            
            // No concurrency limiting! Let all workers run in parallel!
        }

        // Expose target per minute for logging
        public static int GetTargetPerMinute() => _maxPerMinute;

        // Heuristic: suggest a good default worker count based on rate and environment
        public static int GetSuggestedWorkers(bool useApiKey)
        {
            var target = useApiKey ? 3000 : 60;
            var perSec = target / 60.0;
            // IO-bound: allow several requests in flight to hide latency
            var suggested = (int)Math.Ceiling(perSec * 8); // ~8x inflight
            var cpuFactor = Environment.ProcessorCount * (useApiKey ? 16 : 4);
            var cap = useApiKey ? 256 : 16;
            return Math.Max(1, Math.Min(cap, Math.Min(cpuFactor, Math.Max(suggested, useApiKey ? 64 : 4))));
        }
    }
}