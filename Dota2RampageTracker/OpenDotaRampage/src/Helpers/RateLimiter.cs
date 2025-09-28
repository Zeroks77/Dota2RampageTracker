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
    private static long totalDelayMs = 0;
    private static int waitCount = 0;

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
            }
        }

        if (Interlocked.Increment(ref apiCallCount) >= maxCallsPerMinute)
        {
            TimeSpan delay = TimeSpan.FromMinutes(1) - (DateTime.UtcNow - lastApiCallReset);
            Interlocked.Increment(ref waitCount);
            Interlocked.Add(ref totalDelayMs, (long)delay.TotalMilliseconds);
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

    public static (int waits, TimeSpan totalDelay) Snapshot()
    {
        return (waitCount, TimeSpan.FromMilliseconds(Interlocked.Read(ref totalDelayMs)));
    }
}