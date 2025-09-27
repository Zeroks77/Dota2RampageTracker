using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenDotaRampage.Models;

namespace OpenDotaRampage.Helpers
{
    public static class HeroDataFetcher
    {
        public static async Task<(string SteamName, string AvatarUrl)> GetPlayerSteamName(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
            var playerData = JObject.Parse(response);
            string steamName = playerData?["profile"]?["personaname"]?.ToString() ?? $"Player {playerId}";
            string avatarUrl = playerData?["profile"]?["avatarfull"]?.ToString() ?? string.Empty;

            return (steamName, avatarUrl);
        }

        public static async Task<Dictionary<int, Hero>> FetchHeroData(HttpClient client)
        {
            string url = "https://api.opendota.com/api/heroes";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
            var heroes = JsonConvert.DeserializeObject<List<Hero>>(response) ?? new List<Hero>();

            var heroData = new Dictionary<int, Hero>();
            foreach (var hero in heroes)
            {
                heroData[hero.Id] = hero;
            }

            return heroData;
        }
          public static async Task<CountsResponse> GetPlayerCounts(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}/counts";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
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
            var response = await ApiHelper.GetStringWith429Retry(client, url);
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
            string url = "https://api.opendota.com/api/constants/game_mode";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
            var gameModesData = JObject.Parse(response);

            var gameModes = new Dictionary<int, string>();
            foreach (var item in gameModesData)
            {
                var id = int.Parse(item.Key);
                var name = (string?)item.Value?["name"] ?? item.Key;
                gameModes[id] = name;
            }

            return gameModes;
        }

        public static async Task<Dictionary<int, string>> GetLobbyTypes(HttpClient client)
        {
            string url = "https://api.opendota.com/api/constants/lobby_type";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
            var lobbyTypesData = JObject.Parse(response);

            var lobbyTypes = new Dictionary<int, string>();
            foreach (var item in lobbyTypesData)
            {
                var id = int.Parse(item.Key);
                var name = (string?)item.Value?["name"] ?? item.Key;
                lobbyTypes[id] = name;
            }

            return lobbyTypes;
        }

        public static async Task<Dictionary<int, string>> GetPatches(HttpClient client)
        {
            string url = "https://api.opendota.com/api/constants/patch";
            await RateLimiter.EnsureRateLimit();
            var response = await ApiHelper.GetStringWith429Retry(client, url);
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

            return patches;
        }

    }
}