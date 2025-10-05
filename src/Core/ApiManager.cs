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

        // Zentraler Sender mit 429/5xx Backoff und Retry-After Unterstützung
        private async Task<HttpResponseMessage?> SendWithRetryAsync(Func<Task<HttpResponseMessage>> send, string op)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var resp = await send();
                    if (resp.StatusCode == (HttpStatusCode)429)
                    {
                        var retry = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(90, Math.Pow(2, attempt) * 2));
                        try { resp.Dispose(); } catch { }
                        await Task.Delay(retry + TimeSpan.FromMilliseconds(Random.Shared.Next(250, 750)));
                        continue;
                    }
                    if ((int)resp.StatusCode >= 500)
                    {
                        // transient server error -> backoff
                        if (attempt < 2)
                        {
                            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                            try { resp.Dispose(); } catch { }
                            await Task.Delay(delay + TimeSpan.FromMilliseconds(Random.Shared.Next(100, 400)));
                            continue;
                        }
                    }
                    return resp;
                }
                catch (HttpRequestException)
                {
                    if (attempt == 2) return null;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                }
                catch (TaskCanceledException)
                {
                    if (attempt == 2) return null;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)));
                }
            }
            return null;
        }

        public async Task<Match?> GetMatchAsync(long matchId)
        {
            Logger.LogApiRequest("GET /matches/{id}", matchId: matchId);
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/matches/{matchId}");
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetMatchAsync));
            if (resp == null || !resp.IsSuccessStatusCode) 
            {
                Logger.Debug($"Match {matchId} failed: {resp?.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<Match>(json);
        }

        public async Task<PlayerMatchSummary[]?> GetPlayerMatchesAsync(long playerId)
        {
            Logger.LogApiRequest("GET /players/{id}/matches", playerId: playerId);
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/players/{playerId}/matches");
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetPlayerMatchesAsync));
            if (resp == null || !resp.IsSuccessStatusCode) 
            {
                Logger.Debug($"Player {playerId} matches failed: {resp?.StatusCode}");
                return Array.Empty<PlayerMatchSummary>();
            }
            var json = await resp.Content.ReadAsStringAsync();
            var matches = JsonConvert.DeserializeObject<PlayerMatchSummary[]>(json) ?? Array.Empty<PlayerMatchSummary>();
            Logger.Debug($"Player {playerId}: {matches.Length} matches retrieved");
            return matches;
        }

        public async Task<PlayerProfile?> GetPlayerProfileAsync(long playerId)
        {
            Logger.LogApiRequest("GET /players/{id}", playerId: playerId);
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/players/{playerId}");
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetPlayerProfileAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<PlayerProfileResponse>(json);
            return root?.Profile;
        }

        public async Task<bool?> GetHasParsedAsync(long matchId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetHasParsedAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            if (jo != null && jo.TryGetValue("has_parsed", out var v)) return v.Value<bool>();
            return null;
        }

        public async Task<long?> RequestParseAsync(long matchId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            using var resp = await SendWithRetryAsync(() => _http.PostAsync(url, null), nameof(RequestParseAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            return jo?["job"]?["jobId"]?.Value<long?>();
        }

        public async Task<bool?> CheckJobAsync(long jobId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{jobId}");
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(CheckJobAsync));
            if (resp == null) return null;
            if (resp.StatusCode == HttpStatusCode.OK) return true;
            if ((int)resp.StatusCode == 404) return false;
            return null;
        }
    }
}
