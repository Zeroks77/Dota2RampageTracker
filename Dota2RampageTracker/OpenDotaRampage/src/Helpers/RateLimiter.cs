using System;
using System.Threading;
using System.Threading.Tasks;

public static class RateLimiter
{
    private static SemaphoreSlim rateLimitSemaphore;
    public static bool useApiKey = false;
    private static int maxCallsPerMinute = 60; // Default to 60 calls per minute
    private static int apiCallCount = 0;
    private static DateTime lastApiCallReset = DateTime.UtcNow;
    private static TimeSpan resetInterval = TimeSpan.FromMinutes(1);

    public static SemaphoreSlim concurrencyLimiter = new SemaphoreSlim(20, 20); // more parallelism, API limiter governs QPS

    static RateLimiter()
    {
        rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
    }
    public static void Initialize()
    {
        rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
        Console.WriteLine("Rate limiter initialized.");
    }

    public static void SetRateLimit(bool useKey)
    {
        useApiKey = useKey;
        maxCallsPerMinute = useApiKey ? 1000 : 60;
        rateLimitSemaphore = new SemaphoreSlim(maxCallsPerMinute, maxCallsPerMinute);
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

    rateLimitSemaphore.Release();
    }

    public static void ResetRateLimiter()
    {
        lastApiCallReset = DateTime.UtcNow;
        apiCallCount = 0;
    }
}