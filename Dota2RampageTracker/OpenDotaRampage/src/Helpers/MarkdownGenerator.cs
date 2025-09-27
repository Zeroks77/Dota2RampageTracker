using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Newtonsoft.Json;
using OpenDotaRampage.Models;

namespace OpenDotaRampage.Helpers
{
    public static class MarkdownGenerator
    {
        public static void GenerateMarkdown(string steamName, List<Match> allRampageMatches, Dictionary<int, Hero> heroData, int playerId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerId.ToString());
            Directory.CreateDirectory(playerDirectory);
            string filePath = Path.Combine(playerDirectory, "Rampages.md");
            // Delete the existing markdown file if it exists
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine($"Player {steamName} has got {allRampageMatches.Count} total Rampages\n");

                var groupedRampages = allRampageMatches
                    .SelectMany(match => match.Players
                        .Where(player => player.AccountId == playerId && player.HeroId.HasValue) // only entries with hero id
                        .Select(player => new { match.MatchId, HeroId = player.HeroId!.Value, IsNew = allRampageMatches.Any(m => m.MatchId == match.MatchId) }))
                    .GroupBy(x => x.HeroId)
                    .ToDictionary(g => g.Key, g => g.Select(x => new { x.MatchId, x.IsNew }).ToList());

                var sortedGroupedRampages = groupedRampages
                    .OrderBy(g => heroData.ContainsKey(g.Key) ? heroData[g.Key].LocalizedName : $"Hero ID {g.Key} (Name not found)")
                    .ToList();

                foreach (var group in sortedGroupedRampages)
                {
                    int heroId = group.Key;
                    if (heroData.TryGetValue(heroId, out var hero) && hero != null)
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

       
        public static void GenerateMainReadme(Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)> steamProfiles, Dictionary<int, string> gameModes, Dictionary<int, string> lobbyTypes, Dictionary<int, string> patches)
        {
            string rootDirectory = Directory.GetCurrentDirectory();
            string filePath = Path.Combine(rootDirectory, "README.md");

            Console.WriteLine($"Generating main README at: {filePath}");

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine("# Dota 2 Rampage Tracker");
                writer.WriteLine("This repository contains rampage tracking data for various Dota 2 players.\n");

                writer.WriteLine("## Players");
                writer.WriteLine("| Player Name | Profile Picture | Rampage Percentage | Win Rate (Total) | Win Rate (Unranked) | Win Rate (Ranked) | Rampage File |");
                writer.WriteLine("|-------------|-----------------|--------------------|------------------|---------------------|-------------------|--------------|");

                foreach (var steamProfile in steamProfiles)
                {
                    string playerName = steamProfile.Value.SteamName;
                    string playerId  = steamProfile.Key;
                    string avatarUrl = steamProfile.Value.AvatarUrl;
                    var totals = steamProfile.Value.Totals;
                    var counts = steamProfile.Value.Counts;

                    int totalMatches = totals.ContainsKey("matches") ? totals["matches"] : 0;
                    int unrankedMatches = counts.LobbyType.ContainsKey("0") ? counts.LobbyType["0"].Games : 0;
                    int unrankedWins = counts.LobbyType.ContainsKey("0") ? counts.LobbyType["0"].Win : 0;
                    int rankedMatches = counts.LobbyType.ContainsKey("7") ? counts.LobbyType["7"].Games : 0;
                    int rankedWins = counts.LobbyType.ContainsKey("7") ? counts.LobbyType["7"].Win : 0;       
                    int totalWins = rankedWins + unrankedWins;


                    double winRateTotal = totalMatches > 0 ? (double)totalWins / totalMatches * 100 : 0;
                    double winRateUnranked = unrankedMatches > 0 ? (double)unrankedWins / unrankedMatches * 100 : 0;
                    double winRateRanked = rankedMatches > 0 ? (double)rankedWins / rankedMatches * 100 : 0;

                    int rampagesTotal = totals.ContainsKey("rampages") ? totals["rampages"] : 0;
                    string rampageFilePath = Path.Combine(Program.outputDirectory, playerId, "Rampages.md").Replace("\\", "/");
                    writer.WriteLine($"| {playerName} | ![Profile Picture]({avatarUrl}) | {rampagesTotal}/{totalMatches}| {winRateTotal:F2}% | {winRateUnranked:F2}% | {winRateRanked:F2}% | [Rampages](./{rampageFilePath}) |");
                }
            }

            Console.WriteLine("Main README generated successfully.");
        }


        public static List<Match> LoadRampageMatchesFromCache(string playerName)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");

            if (File.Exists(cacheFilePath))
            {
                var jsonData = File.ReadAllText(cacheFilePath);
                return JsonConvert.DeserializeObject<List<Match>>(jsonData) ?? new List<Match>();
            }

            return new List<Match>();
        }
    }
}