using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using OpenDotaRampage.Models;

namespace OpenDotaRampage.Helpers
{
    public static class MarkdownGenerator
    {
        public static void GenerateMarkdown(string steamName, List<Match> newRampageMatches, Dictionary<int, Hero> heroData, int playerId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, steamName);
            Directory.CreateDirectory(playerDirectory);
            string filePath = Path.Combine(playerDirectory, "Rampages.md");

            // Load cached rampage matches
            var cachedRampageMatches = LoadRampageMatchesFromCache(steamName);

            var allRampageMatches = newRampageMatches.Concat(cachedRampageMatches).ToList();

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"Player {steamName} has got {allRampageMatches.Count} total Rampages\n");

                var groupedRampages = allRampageMatches
                    .SelectMany(match => match.Players
                        .Where(player => player.AccountId == playerId) // Filter to include only the player's hero
                        .Select(player => new { match.MatchId, player.HeroId }))
                    .GroupBy(x => x.HeroId)
                    .ToDictionary(g => (int)g.Key, g => g.Select(x => x.MatchId).ToList());

                var sortedGroupedRampages = groupedRampages
                    .OrderBy(g => heroData.ContainsKey(g.Key) ? heroData[g.Key].LocalizedName : $"Hero ID {g.Key} (Name not found)")
                    .ToList();

                foreach (var group in sortedGroupedRampages)
                {
                    int heroId = group.Key;
                    if (heroData.TryGetValue(heroId, out Hero hero))
                    {
                        string heroName = hero.Name.Replace("npc_dota_hero_", "");
                        string heroIconUrl = $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{heroName}.png";

                        writer.WriteLine($"### {hero.LocalizedName}");
                        writer.WriteLine($"![{hero.LocalizedName}]({heroIconUrl})\n");

                        writer.WriteLine("| Match ID |");
                        writer.WriteLine("|----------|");

                        foreach (var matchId in group.Value)
                        {
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{matchId}) |");
                        }

                        writer.WriteLine();
                    }
                    else
                    {
                        writer.WriteLine($"### Hero ID {heroId} (Name not found)");
                        writer.WriteLine("| Match ID |");
                        writer.WriteLine("|----------|");

                        foreach (var matchId in group.Value)
                        {
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{matchId}) |");
                        }

                        writer.WriteLine();
                    }
                }
            }
        }

        private static List<Match> LoadRampageMatchesFromCache(string playerName)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");

            if (File.Exists(cacheFilePath))
            {
                var jsonData = File.ReadAllText(cacheFilePath);
                return JsonConvert.DeserializeObject<List<Match>>(jsonData);
            }

            return new List<Match>();
        }
    }
}