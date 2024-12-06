using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDotaRampage.Helpers
{
    public static class RateLimiter
    {
        public static readonly SemaphoreSlim concurrencyLimiter = new SemaphoreSlim(10); // Increase concurrency limit
        private static readonly int maxCallsPerMinute = 1100;
        private static int apiCallCount = 0;
        private static DateTime lastApiCallReset = DateTime.UtcNow;
        private static readonly SemaphoreSlim rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
        private static Timer rateLimitResetTimer;
        private static readonly TimeSpan resetInterval = TimeSpan.FromMinutes(1);

        public static void Initialize()
        {
            // Initialize the rate limit reset timer
            rateLimitResetTimer = new Timer(ResetRateLimiter, null, resetInterval, resetInterval);
            Console.WriteLine("Rate limiter initialized.");
        }

        public static async Task EnsureRateLimit()
        {
            await rateLimitSemaphore.WaitAsync();

            TimeSpan timeUntilReset = resetInterval - (DateTime.UtcNow - lastApiCallReset);

            lock (concurrencyLimiter)
            {
                if ((DateTime.UtcNow - lastApiCallReset).TotalMinutes >= 1)
                {
                    lastApiCallReset = DateTime.UtcNow;
                    apiCallCount = 0;
                    Console.WriteLine("Rate limiter reset due to time interval.");
                }
            }

            if (Interlocked.Increment(ref apiCallCount) >= maxCallsPerMinute)
            {
                TimeSpan delay = TimeSpan.FromMinutes(1) - (DateTime.UtcNow - lastApiCallReset);
                Console.WriteLine($"\r\nRate limit exceeded. Delaying for {delay.TotalSeconds} seconds.");
                await Task.Delay(delay);
                lock (concurrencyLimiter)
                {
                    lastApiCallReset = DateTime.UtcNow;
                    apiCallCount = 0;
                }
            }
        }

        private static void ResetRateLimiter(object state)
        {
            lock (concurrencyLimiter)
            {
                lastApiCallReset = DateTime.UtcNow;
                apiCallCount = 0;
                int releaseCount = maxCallsPerMinute - rateLimitSemaphore.CurrentCount;
                if (releaseCount > 0)
                {
                    rateLimitSemaphore.Release(releaseCount);
                }
            }
        }
    }
}