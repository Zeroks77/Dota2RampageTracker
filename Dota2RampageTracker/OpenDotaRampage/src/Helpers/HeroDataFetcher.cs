using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenDotaRampage.Models;
using System.IO;
using System;

namespace OpenDotaRampage.Helpers
{
    public static class HeroDataFetcher
    {
        private static string CacheDir
        {
            get
            {
                var root = Directory.GetParent(Program.outputDirectory)!.FullName;
                var path = Path.Combine(root, ".cache");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        private static T? TryReadCache<T>(string name, TimeSpan ttl)
        {
            try
            {
                var path = Path.Combine(CacheDir, name + ".json");
                if (!File.Exists(path)) return default;
                var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                if (age > ttl) return default;
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<T>(json);
            }
            catch { return default; }
        }

        private static void WriteCache<T>(string name, T data)
        {
            try
            {
                var path = Path.Combine(CacheDir, name + ".json");
                var json = JsonConvert.SerializeObject(data, Formatting.Indented);
                File.WriteAllText(path, json);
            }
            catch { }
        }
        public static async Task<(string SteamName, string AvatarUrl)> GetPlayerSteamName(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var playerData = JObject.Parse(response);
            string steamName = playerData?["profile"]?["personaname"]?.ToString() ?? $"Player {playerId}";
            string avatarUrl = playerData?["profile"]?["avatarfull"]?.ToString() ?? string.Empty;

            return (steamName, avatarUrl);
        }

        public static async Task<Dictionary<int, Hero>> FetchHeroData(HttpClient client)
        {
            var cached = TryReadCache<Dictionary<int, Hero>>("heroes", TimeSpan.FromHours(24));
            if (cached != null && cached.Count > 0) return cached;
            string url = "https://api.opendota.com/api/heroes";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var heroes = JsonConvert.DeserializeObject<List<Hero>>(response) ?? new List<Hero>();

            var heroData = new Dictionary<int, Hero>();
            foreach (var hero in heroes)
            {
                heroData[hero.Id] = hero;
            }
            WriteCache("heroes", heroData);
            return heroData;
        }
          public static async Task<CountsResponse> GetPlayerCounts(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}/counts";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            return JsonConvert.DeserializeObject<CountsResponse>(response) ?? new CountsResponse
            {
                GameMode = new Dictionary<string, WinLoss>(),
                LobbyType = new Dictionary<string, WinLoss>(),
                LaneRole = new Dictionary<string, WinLoss>(),
                Region = new Dictionary<string, WinLoss>(),
                Patch = new Dictionary<string, WinLoss>(),
                IsRadiant = new Dictionary<string, WinLoss>(),
                LeaverStatus = new Dictionary<string, WinLoss>()
            };
        }
        public static async Task<Dictionary<string, int>> GetPlayerTotals(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}/totals";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var totalsData = JArray.Parse(response);

            var totals = new Dictionary<string, int>();
            if (totalsData.Count > 0 && totalsData[0] != null && totalsData[0]["n"] != null)
            {
                totals["matches"] = (int)totalsData[0]["n"]!;
            }
            foreach (var item in totalsData)
            {
                var fieldToken = item?["field"];
                var sumToken = item?["sum"];
                if (fieldToken != null && sumToken != null)
                {
                    var field = (string)fieldToken!;
                    var n = (int)sumToken!;
                    totals[field] = n;
                }
            }

            return totals;
        }
        public static async Task<Dictionary<int, string>> GetGameModes(HttpClient client)
        {
            var cached = TryReadCache<Dictionary<int, string>>("game_modes", TimeSpan.FromHours(24));
            if (cached != null && cached.Count > 0) return cached;
            string url = "https://api.opendota.com/api/constants/game_mode";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var gameModesData = JObject.Parse(response);

            var gameModes = new Dictionary<int, string>();
            foreach (var item in gameModesData)
            {
                var id = int.Parse(item.Key);
                var name = (string?)item.Value?["name"] ?? item.Key;
                gameModes[id] = name;
            }
            WriteCache("game_modes", gameModes);
            return gameModes;
        }

        public static async Task<Dictionary<int, string>> GetLobbyTypes(HttpClient client)
        {
            var cached = TryReadCache<Dictionary<int, string>>("lobby_types", TimeSpan.FromHours(24));
            if (cached != null && cached.Count > 0) return cached;
            string url = "https://api.opendota.com/api/constants/lobby_type";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var lobbyTypesData = JObject.Parse(response);

            var lobbyTypes = new Dictionary<int, string>();
            foreach (var item in lobbyTypesData)
            {
                var id = int.Parse(item.Key);
                var name = (string?)item.Value?["name"] ?? item.Key;
                lobbyTypes[id] = name;
            }
            WriteCache("lobby_types", lobbyTypes);
            return lobbyTypes;
        }

        public static async Task<Dictionary<int, string>> GetPatches(HttpClient client)
        {
            var cached = TryReadCache<Dictionary<int, string>>("patches", TimeSpan.FromHours(24));
            if (cached != null && cached.Count > 0) return cached;
            string url = "https://api.opendota.com/api/constants/patch";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWithBackoff(client, url);
            var patchesData = JArray.Parse(response);

            var patches = new Dictionary<int, string>();
            foreach (var item in patchesData)
            {
                var idToken = item?["id"];
                var nameToken = item?["name"];
                if (idToken != null)
                {
                    var id = (int)idToken!;
                    var name = (string?)nameToken ?? id.ToString();
                    patches[id] = name;
                }
            }
            WriteCache("patches", patches);
            return patches;
        }

    }
}