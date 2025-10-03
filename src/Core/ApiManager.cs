using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RampageTracker.Models;

namespace RampageTracker.Core
{
    public class ApiManager
    {
        private readonly HttpClient _http;
        private readonly string? _apiKey;
        private const string BaseUrl = "https://api.opendota.com/api";
        private const int MaxAttempts = 3;

        public ApiManager(HttpClient http, string? apiKey)
        {
            _http = http;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }

        private string WithKey(string url)
        {
            if (string.IsNullOrWhiteSpace(_apiKey)) return url;
            return url.Contains("?") ? $"{url}&api_key={_apiKey}" : $"{url}?api_key={_apiKey}";
        }

        private async Task<HttpResponseMessage?> SendWithRetriesAsync(Func<Task<HttpResponseMessage>> send)
        {
            var attempt = 0;
            var delayMs = 500;
            while (true)
            {
                attempt++;
                try
                {
                    await RateLimiter.EnsureRateAsync();
                    var resp = await send();
                    if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        // Honor Retry-After if present
                        var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                        Logger.Info($"429 received, retrying in {retryAfter.TotalSeconds:F0}s...");
                        await Task.Delay(retryAfter);
                        if (attempt < MaxAttempts) continue;
                        return resp; // give up, caller decides how to handle
                    }
                    if ((int)resp.StatusCode >= 500 && (int)resp.StatusCode <= 599)
                    {
                        if (attempt < MaxAttempts)
                        {
                            Logger.Warn($"Server { (int)resp.StatusCode }, retry {attempt}/{MaxAttempts} in {delayMs}ms");
                            await Task.Delay(delayMs);
                            delayMs *= 2;
                            continue;
                        }
                    }
                    return resp;
                }
                catch (TaskCanceledException ex)
                {
                    if (attempt < MaxAttempts)
                    {
                        Logger.Warn($"Timeout: {ex.Message}. Retry {attempt}/{MaxAttempts} in {delayMs}ms");
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                        continue;
                    }
                    Logger.Error($"HTTP timeout after {attempt} attempts: {ex.Message}");
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    if (attempt < MaxAttempts)
                    {
                        Logger.Warn($"Network error: {ex.Message}. Retry {attempt}/{MaxAttempts} in {delayMs}ms");
                        await Task.Delay(delayMs);
                        delayMs *= 2;
                        continue;
                    }
                    Logger.Error($"HTTP error after {attempt} attempts: {ex.Message}");
                    return null;
                }
            }
        }

        public async Task<Match?> GetMatchAsync(long matchId)
        {
            var url = WithKey($"{BaseUrl}/matches/{matchId}");
            var resp = await SendWithRetriesAsync(() => _http.GetAsync(url));
            if (resp == null) return null;
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<Match>(json);
        }

        public async Task<PlayerMatchSummary[]?> GetPlayerMatchesAsync(long playerId)
        {
            var url = WithKey($"{BaseUrl}/players/{playerId}/matches");
            var resp = await SendWithRetriesAsync(() => _http.GetAsync(url));
            if (resp == null) return System.Array.Empty<PlayerMatchSummary>();
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return System.Array.Empty<PlayerMatchSummary>();
            if (!resp.IsSuccessStatusCode) return System.Array.Empty<PlayerMatchSummary>();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<PlayerMatchSummary[]>(json) ?? System.Array.Empty<PlayerMatchSummary>();
        }

        public async Task<bool?> GetHasParsedAsync(long matchId)
        {
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            var resp = await SendWithRetriesAsync(() => _http.GetAsync(url));
            if (resp == null) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            if (jo != null && jo.TryGetValue("has_parsed", out var v)) return v.Value<bool>();
            return null;
        }

        public async Task<long?> RequestParseAsync(long matchId)
        {
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            var resp = await SendWithRetriesAsync(() => _http.PostAsync(url, null));
            if (resp == null) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            return jo?["job"]?["jobId"]?.Value<long?>();
        }

        public async Task<bool?> CheckJobAsync(long jobId)
        {
            var url = WithKey($"{BaseUrl}/request/{jobId}");
            var resp = await SendWithRetriesAsync(() => _http.GetAsync(url));
            if (resp == null) return null;
            if (resp.StatusCode == HttpStatusCode.OK) return true;
            if ((int)resp.StatusCode == 404) return false;
            return null;
        }
    }
}
