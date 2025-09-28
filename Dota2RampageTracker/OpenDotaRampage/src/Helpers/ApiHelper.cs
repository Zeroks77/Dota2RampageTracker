using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenDotaRampage.Helpers
{
    public static class ApiHelper
    {
        public static volatile bool IsDegradedMode = false;

        public class CircuitOpenException : Exception
        {
            public CircuitOpenException(string message) : base(message) {}
        }

        private class EndpointState
        {
            public int Consecutive429;
            public int Consecutive5xx;
            public DateTimeOffset? OpenUntil;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, EndpointState> _circuits = new();

        private static string NormalizeKey(string url)
        {
            try
            {
                var u = new Uri(url);
                // e.g., /api/players/{id}/matches -> /api/players/*/matches
                var path = u.AbsolutePath;
                // Basic anonymization: replace numbers with *
                var norm = System.Text.RegularExpressions.Regex.Replace(path, @"\d+", "*");
                return norm.ToLowerInvariant();
            }
            catch { return url; }
        }

        private static bool BeforeRequest(string key)
        {
            var st = _circuits.GetOrAdd(key, _ => new EndpointState());
            if (st.OpenUntil.HasValue && st.OpenUntil.Value > DateTimeOffset.UtcNow)
            {
                IsDegradedMode = true;
                return false;
            }
            return true;
        }

        private static void AfterSuccess(string key)
        {
            var st = _circuits.GetOrAdd(key, _ => new EndpointState());
            st.Consecutive429 = 0;
            st.Consecutive5xx = 0;
            st.OpenUntil = null;
        }

        private static void AfterFailure(string key, HttpStatusCode code)
        {
            var st = _circuits.GetOrAdd(key, _ => new EndpointState());
            if (code == HttpStatusCode.TooManyRequests)
            {
                st.Consecutive429++;
                if (st.Consecutive429 >= 5)
                {
                    st.OpenUntil = DateTimeOffset.UtcNow.AddMinutes(2);
                    IsDegradedMode = true;
                }
            }
            else if ((int)code >= 500)
            {
                st.Consecutive5xx++;
                if (st.Consecutive5xx >= 3)
                {
                    st.OpenUntil = DateTimeOffset.UtcNow.AddMinutes(1);
                    IsDegradedMode = true;
                }
            }
        }
        public static class Metrics
        {
            private static int _ok;
            private static int _r429;
            private static int _r5xx;
            private static long _bytes;
            private static long _latencyMsSum;
            private static int _req;
            public static void IncOk() => System.Threading.Interlocked.Increment(ref _ok);
            public static void Inc429() => System.Threading.Interlocked.Increment(ref _r429);
            public static void Inc5xx() => System.Threading.Interlocked.Increment(ref _r5xx);
            public static void AddBytes(long n) => System.Threading.Interlocked.Add(ref _bytes, n);
            public static void AddLatency(long ms)
            {
                System.Threading.Interlocked.Add(ref _latencyMsSum, ms);
                System.Threading.Interlocked.Increment(ref _req);
            }
            public static (int ok, int r429, int r5xx, long bytes, double avgLatencyMs, int requests) Snapshot()
            {
                var req = System.Threading.Interlocked.CompareExchange(ref _req, 0, 0);
                var lat = System.Threading.Interlocked.Read(ref _latencyMsSum);
                double avg = req > 0 ? (double)lat / req : 0;
                return (_ok, _r429, _r5xx, System.Threading.Interlocked.Read(ref _bytes), avg, req);
            }
        }
        public static string AppendApiKey(string url)
        {
            var key = Program.apiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return url;
            }
            var separator = url.Contains("?") ? "&" : "?";
            return url + separator + "api_key=" + Uri.EscapeDataString(key);
        }

        public static async Task<string> GetStringWithBackoff(HttpClient client, string url, int maxRetries = 4)
        {
            url = AppendApiKey(url);
            var key = NormalizeKey(url);
            if (!BeforeRequest(key)) throw new CircuitOpenException($"Circuit open for {key}");
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    // Throttle retries as well to respect the global rate limit
                    await RateLimiter.EnsureRateLimit();
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await client.GetAsync(url);
                sw.Stop();
                Metrics.AddLatency((long)sw.ElapsedMilliseconds);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Metrics.Inc429();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                if ((int)resp.StatusCode >= 500)
                {
                    Metrics.Inc5xx();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                Metrics.IncOk();
                AfterSuccess(key);
                return await resp.Content.ReadAsStringAsync();
            }
            // Last attempt (throttled)
            await RateLimiter.EnsureRateLimit();
            var last = await client.GetAsync(url);
            if (last.StatusCode == HttpStatusCode.TooManyRequests) Metrics.Inc429();
            else if ((int)last.StatusCode >= 500) Metrics.Inc5xx();
            last.EnsureSuccessStatusCode();
            Metrics.IncOk();
            AfterSuccess(key);
            return await last.Content.ReadAsStringAsync();
        }

        public static async Task<HttpResponseMessage> GetAsyncWithBackoff(HttpClient client, string url, int maxRetries = 4)
        {
            url = AppendApiKey(url);
            var key = NormalizeKey(url);
            if (!BeforeRequest(key)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await RateLimiter.EnsureRateLimit();
                }
                var resp = await client.GetAsync(url);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Metrics.Inc429();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                if ((int)resp.StatusCode >= 500)
                {
                    Metrics.Inc5xx();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                Metrics.IncOk();
                if (resp.Content?.Headers?.ContentLength.HasValue == true)
                {
                    Metrics.AddBytes(resp.Content.Headers.ContentLength.Value);
                }
                AfterSuccess(key);
                return resp;
            }
            await RateLimiter.EnsureRateLimit();
            var swLast = System.Diagnostics.Stopwatch.StartNew();
            var last = await client.GetAsync(url);
            swLast.Stop();
            Metrics.AddLatency((long)swLast.ElapsedMilliseconds);
            if (last.StatusCode == HttpStatusCode.TooManyRequests) Metrics.Inc429();
            else if ((int)last.StatusCode >= 500) Metrics.Inc5xx();
            else Metrics.IncOk();
            if (last.IsSuccessStatusCode) AfterSuccess(key); else AfterFailure(key, last.StatusCode);
            return last;
        }

        public static async Task<HttpResponseMessage> PostWithBackoff(HttpClient client, string url, HttpContent? content = null, int maxRetries = 4)
        {
            url = AppendApiKey(url);
            var key = NormalizeKey(url);
            if (!BeforeRequest(key)) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                if (attempt > 0)
                {
                    await RateLimiter.EnsureRateLimit();
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var resp = await client.PostAsync(url, content);
                sw.Stop();
                Metrics.AddLatency((long)sw.ElapsedMilliseconds);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    Metrics.Inc429();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                if ((int)resp.StatusCode >= 500)
                {
                    Metrics.Inc5xx();
                    AfterFailure(key, resp.StatusCode);
                    await Task.Delay(BackoffDelay(attempt));
                    continue;
                }
                Metrics.IncOk();
                if (resp.Content?.Headers?.ContentLength.HasValue == true)
                {
                    Metrics.AddBytes(resp.Content.Headers.ContentLength.Value);
                }
                AfterSuccess(key);
                return resp;
            }
            await RateLimiter.EnsureRateLimit();
            var swLast = System.Diagnostics.Stopwatch.StartNew();
            var last = await client.PostAsync(url, content);
            swLast.Stop();
            Metrics.AddLatency((long)swLast.ElapsedMilliseconds);
            if (last.StatusCode == HttpStatusCode.TooManyRequests) Metrics.Inc429();
            else if ((int)last.StatusCode >= 500) Metrics.Inc5xx();
            else Metrics.IncOk();
            if (last.IsSuccessStatusCode) AfterSuccess(key); else AfterFailure(key, last.StatusCode);
            return last;
        }

        private static int BackoffDelay(int attempt)
        {
            // Exponentielles Backoff mit Jitter (Basis 750ms, max ~10s)
            var baseMs = 750 * Math.Pow(2, Math.Min(attempt, 4));
            var jitter = new Random().Next(0, 400);
            return (int)Math.Min(10000, baseMs + jitter);
        }
    }
}
