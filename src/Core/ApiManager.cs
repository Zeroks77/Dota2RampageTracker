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
        private readonly ApiKeyUsageTracker? _keyTracker;
        private const string BaseUrl = "https://api.opendota.com/api";

        public ApiManager(HttpClient http, string? apiKey, ApiKeyUsageTracker? keyTracker = null)
        {
            _http = http;
            _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
            _keyTracker = keyTracker;
        }

        private string WithKey(string url, bool includeKey)
        {
            if (!includeKey || string.IsNullOrWhiteSpace(_apiKey)) return url;
            return url.Contains("?") ? $"{url}&api_key={_apiKey}" : $"{url}?api_key={_apiKey}";
        }

        private bool ShouldUseKeyForRequest()
        {
            if (_keyTracker == null) return false;
            var useKey = _keyTracker.ShouldUseKey(out var activated);
            if (activated)
            {
                RateLimiter.UpdateApiKeyMode(useApiKey: true);
                Logger.Info("API key activated after daily threshold; switching to higher rate limit.");
            }
            return useKey;
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
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/matches/{matchId}", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetMatchAsync));
            if (resp == null || !resp.IsSuccessStatusCode) 
            {
                Logger.Debug($"Match {matchId} failed: {resp?.StatusCode}");
                return null;
            }
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<Match>(json);
        }

        public async Task<PlayerMatchSummary[]?> GetPlayerMatchesAsync(long playerId, int? limit = null, int? offset = null)
        {
            Logger.LogApiRequest("GET /players/{id}/matches", playerId: playerId);
            await RateLimiter.EnsureRateAsync();
            var useKey = ShouldUseKeyForRequest();
            string BuildUrl(int? effectiveLimit)
            {
                var url = $"{BaseUrl}/players/{playerId}/matches";
                var hasQuery = false;
                if (effectiveLimit.HasValue)
                {
                    url += hasQuery ? "&" : "?"; hasQuery = true;
                    url += $"limit={effectiveLimit.Value}";
                }
                if (offset.HasValue)
                {
                    url += hasQuery ? "&" : "?"; hasQuery = true;
                    url += $"offset={offset.Value}";
                }
                return WithKey(url, includeKey: useKey);
            }

            var effectiveLimit = limit;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var url = BuildUrl(effectiveLimit);
                using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetPlayerMatchesAsync));
                if (resp == null || !resp.IsSuccessStatusCode)
                {
                    if (resp != null && resp.StatusCode == HttpStatusCode.BadRequest && effectiveLimit.HasValue && effectiveLimit.Value > 100)
                    {
                        Logger.Warn($"Player {playerId} matches failed: {resp.StatusCode} (limit={effectiveLimit}); retrying with limit=100");
                        effectiveLimit = 100;
                        continue;
                    }
                    if (resp != null)
                    {
                        var body = string.Empty;
                        try { body = await resp.Content.ReadAsStringAsync(); } catch { }
                        if (!string.IsNullOrWhiteSpace(body))
                        {
                            var trimmed = body.Length > 600 ? body.Substring(0, 600) + "..." : body;
                            Logger.Debug($"Player {playerId} matches failed: {resp.StatusCode} Body: {trimmed}");
                        }
                        else
                        {
                            Logger.Debug($"Player {playerId} matches failed: {resp.StatusCode}");
                        }
                    }
                    else
                    {
                        Logger.Debug($"Player {playerId} matches failed: {resp?.StatusCode}");
                    }
                    return null;
                }
                var json = await resp.Content.ReadAsStringAsync();
                var matches = JsonConvert.DeserializeObject<PlayerMatchSummary[]>(json) ?? Array.Empty<PlayerMatchSummary>();
                Logger.Debug($"Player {playerId}: {matches.Length} matches retrieved");
                return matches;
            }

            return null;
        }

        public async Task<PlayerProfile?> GetPlayerProfileAsync(long playerId)
        {
            Logger.LogApiRequest("GET /players/{id}", playerId: playerId);
            await RateLimiter.EnsureRateAsync();
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/players/{playerId}", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetPlayerProfileAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var root = JsonConvert.DeserializeObject<PlayerProfileResponse>(json);
            return root?.Profile;
        }

        public async Task<bool?> GetHasParsedAsync(long matchId)
        {
            Logger.LogApiRequest("GET /request/{matchId}", matchId: matchId);
            await RateLimiter.EnsureRateAsync();
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/request/{matchId}", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetHasParsedAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            if (jo != null && jo.TryGetValue("has_parsed", out var v)) return v.Value<bool>();
            return null;
        }

        public async Task<long?> RequestParseAsync(long matchId)
        {
            Logger.LogApiRequest("POST /request/{matchId}", matchId: matchId);
            await RateLimiter.EnsureRateAsync();
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/request/{matchId}", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.PostAsync(url, null), nameof(RequestParseAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            return jo?["job"]?["jobId"]?.Value<long?>();
        }

        public async Task<bool?> CheckJobAsync(long jobId)
        {
            Logger.LogApiRequest("GET /request/{jobId}", matchId: jobId);
            await RateLimiter.EnsureRateAsync();
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/request/{jobId}", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(CheckJobAsync));
            if (resp == null) return null;
            if (resp.StatusCode == HttpStatusCode.OK) return true;
            if ((int)resp.StatusCode == 404) return false;
            return null;
        }
        
        public async Task<List<HeroRef>?> GetHeroesAsync()
        {
            Logger.LogApiRequest("GET /heroes");
            var useKey = ShouldUseKeyForRequest();
            var url = WithKey($"{BaseUrl}/heroes", includeKey: useKey);
            using var resp = await SendWithRetryAsync(() => _http.GetAsync(url), nameof(GetHeroesAsync));
            if (resp == null || !resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            try
            {
                return JsonConvert.DeserializeObject<List<HeroRef>>(json) ?? new List<HeroRef>();
            }
            catch
            {
                return new List<HeroRef>();
            }
        }
    }
}
