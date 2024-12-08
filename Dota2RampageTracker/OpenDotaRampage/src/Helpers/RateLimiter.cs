using System;
using System.Threading;
using System.Threading.Tasks;

namespace OpenDotaRampage.Helpers
{
    public static class RateLimiter
    {
        public static readonly SemaphoreSlim concurrencyLimiter = new SemaphoreSlim(10);
        private static int maxCallsPerMinute = 60;
        private static int apiCallCount = 0;
        private static DateTime lastApiCallReset = DateTime.UtcNow;
        private static SemaphoreSlim rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
        private static Timer rateLimitResetTimer;
        private static readonly TimeSpan resetInterval = TimeSpan.FromMinutes(1);
        public static bool useApiKey = false;

        public static void Initialize()
        {
            rateLimitResetTimer = new Timer(ResetRateLimiter, null, resetInterval, resetInterval);
            Console.WriteLine("Rate limiter initialized.");
        }

        public static void SetRateLimit(bool useKey)
        {
            useApiKey = useKey;
            maxCallsPerMinute = useKey ? 1100 : 60;

            // Adjust the semaphore count to match the new rate limit
            lock (concurrencyLimiter)
            {
                rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
            }

            Console.WriteLine($"Rate limit set to {maxCallsPerMinute} calls per minute. Use API key: {useKey}");
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
                rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
                Console.WriteLine($"Rate limiter reset at {DateTime.UtcNow}. Semaphore count set to {maxCallsPerMinute}.");
            }
        }
    }
}