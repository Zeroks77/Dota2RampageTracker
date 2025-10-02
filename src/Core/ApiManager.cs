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

        public async Task<Match?> GetMatchAsync(long matchId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/matches/{matchId}");
            var resp = await _http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return null;
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<Match>(json);
        }

        public async Task<PlayerMatchSummary[]?> GetPlayerMatchesAsync(long playerId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/players/{playerId}/matches");
            var resp = await _http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests) return System.Array.Empty<PlayerMatchSummary>();
            if (!resp.IsSuccessStatusCode) return System.Array.Empty<PlayerMatchSummary>();
            var json = await resp.Content.ReadAsStringAsync();
            return JsonConvert.DeserializeObject<PlayerMatchSummary[]>(json) ?? System.Array.Empty<PlayerMatchSummary>();
        }

        public async Task<bool?> GetHasParsedAsync(long matchId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            if (jo != null && jo.TryGetValue("has_parsed", out var v)) return v.Value<bool>();
            return null;
        }

        public async Task<long?> RequestParseAsync(long matchId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{matchId}");
            var resp = await _http.PostAsync(url, null);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var jo = JsonConvert.DeserializeObject<JObject>(json);
            return jo?["job"]?["jobId"]?.Value<long?>();
        }

        public async Task<bool?> CheckJobAsync(long jobId)
        {
            await RateLimiter.EnsureRateAsync();
            var url = WithKey($"{BaseUrl}/request/{jobId}");
            var resp = await _http.GetAsync(url);
            if (resp.StatusCode == HttpStatusCode.OK) return true;
            if ((int)resp.StatusCode == 404) return false;
            return null;
        }
    }
}
