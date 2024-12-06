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

        public static async Task<string> GetPlayerSteamName(HttpClient client, long playerId)
        {
            string url = $"https://api.opendota.com/api/players/{playerId}";
            var response = await client.GetStringAsync(url);
            var playerData = JObject.Parse(response);
            return playerData["profile"]?["personaname"]?.ToString() ?? "Unknown Player";
        }
    }
}