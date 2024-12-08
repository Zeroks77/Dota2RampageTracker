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
    }
}