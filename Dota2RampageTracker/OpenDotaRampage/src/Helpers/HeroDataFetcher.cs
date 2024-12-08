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
            var response = await client.GetStringAsync(url);
            var playerData = JObject.Parse(response);

            string steamName = playerData["profile"]["personaname"].ToString();
            string avatarUrl = playerData["profile"]["avatarfull"].ToString();

            return (steamName, avatarUrl);
        }

        public static async Task<Dictionary<int, Hero>> FetchHeroData(HttpClient client)
        {
            string url = "https://api.opendota.com/api/heroes";
            var response = await client.GetStringAsync(url);
            var heroes = JsonConvert.DeserializeObject<List<Hero>>(response);

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
            var response = await client.GetStringAsync(url);
            return JsonConvert.DeserializeObject<CountsResponse>(response);
        }
        public static async Task<Dictionary<string, int>> GetPlayerTotals(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}/totals";
            var response = await client.GetStringAsync(url);
            var totalsData = JArray.Parse(response);

            var totals = new Dictionary<string, int>();
            totals["matches"] = (int)totalsData[0]["n"];
            foreach (var item in totalsData)
            {
                var field = (string)item["field"];
                var n = (int)item["sum"];
                totals[field] = n;
            }

            return totals;
        }
        public static async Task<Dictionary<int, string>> GetGameModes(HttpClient client)
        {
            string url = "https://api.opendota.com/api/constants/game_mode";
            var response = await client.GetStringAsync(url);
            var gameModesData = JObject.Parse(response);

            var gameModes = new Dictionary<int, string>();
            foreach (var item in gameModesData)
            {
                var id = int.Parse(item.Key);
                var name = (string)item.Value["name"];
                gameModes[id] = name;
            }

            return gameModes;
        }

        public static async Task<Dictionary<int, string>> GetLobbyTypes(HttpClient client)
        {
            string url = "https://api.opendota.com/api/constants/lobby_type";
            var response = await client.GetStringAsync(url);
            var lobbyTypesData = JObject.Parse(response);

            var lobbyTypes = new Dictionary<int, string>();
            foreach (var item in lobbyTypesData)
            {
                var id = int.Parse(item.Key);
                var name = (string)item.Value["name"];
                lobbyTypes[id] = name;
            }

            return lobbyTypes;
        }

        public static async Task<Dictionary<int, string>> GetPatches(HttpClient client)
        {
            string url = "https://api.opendota.com/api/constants/patch";
            var response = await client.GetStringAsync(url);
            var patchesData = JArray.Parse(response);

            var patches = new Dictionary<int, string>();
            foreach (var item in patchesData)
            {
                var id = (int)item["id"];
                var name = (string)item["name"];;
                patches[id] = name;
            }

            return patches;
        }

    }
}