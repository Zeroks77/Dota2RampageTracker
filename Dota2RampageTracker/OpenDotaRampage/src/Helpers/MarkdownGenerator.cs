using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Delete the existing markdown file if it exists
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // Load cached rampage matches
            var cachedRampageMatches = LoadRampageMatchesFromCache(steamName);

            // Combine new and cached rampage matches, ensuring distinct matches
            var allRampageMatches = newRampageMatches.Concat(cachedRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .Distinct()
                .ToList();

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"Player {steamName} has got {allRampageMatches.Count} total Rampages\n");

                var groupedRampages = allRampageMatches
                    .SelectMany(match => match.Players
                        .Where(player => player.AccountId == playerId) // Filter to include only the player's hero
                        .Select(player => new { match.MatchId, player.HeroId, IsNew = newRampageMatches.Any(m => m.MatchId == match.MatchId) }))
                    .GroupBy(x => x.HeroId)
                    .ToDictionary(g => (int)g.Key, g => g.Select(x => new { x.MatchId, x.IsNew }).ToList());

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

                        writer.WriteLine("| Match ID | Source |");
                        writer.WriteLine("|----------|--------|");

                        foreach (var match in group.Value)
                        {
                            string source = match.IsNew ? "New" : "Cached";
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{match.MatchId}) | {source} |");
                        }

                        writer.WriteLine();
                    }
                    else
                    {
                        writer.WriteLine($"### Hero ID {heroId} (Name not found)");
                        writer.WriteLine("| Match ID | Source |");
                        writer.WriteLine("|----------|--------|");

                        foreach (var match in group.Value)
                        {
                            string source = match.IsNew ? "New" : "Cached";
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{match.MatchId}) | {source} |");
                        }

                        writer.WriteLine();
                    }
                }
            }
        }

         public static void GenerateMainReadme(Dictionary<string, string> steamNames)
        {
            string rootDirectory = Directory.GetCurrentDirectory();
            string filePath = Path.Combine(rootDirectory, "README.md");

            Console.WriteLine($"Generating main README at: {filePath}");

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine("# Dota 2 Rampage Tracker");
                writer.WriteLine("This repository contains rampage tracking data for various Dota 2 players.\n");

                writer.WriteLine("## Players");
                writer.WriteLine("| Player Name | Rampage File |");
                writer.WriteLine("|-------------|---------------|");

                foreach (var steamName in steamNames)
                {
                    string playerName = steamName.Value;
                    string rampageFilePath = Path.Combine(Program.outputDirectory, steamName.Value, "Rampages.md").Replace("\\", "/");
                    writer.WriteLine($"| {playerName} | [Rampages](./{rampageFilePath}) |");
                }
            }

            Console.WriteLine("Main README generated successfully.");
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