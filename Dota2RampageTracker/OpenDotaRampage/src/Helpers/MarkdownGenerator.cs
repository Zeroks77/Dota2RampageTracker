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
                // Only include matches where this player truly has a Rampage (MultiKills contains key 5)
                var rampageEntries = allRampageMatches
                    .SelectMany(match => (match.Players ?? new List<Player>())
                        .Where(player => player.AccountId == playerId && player.HeroId.HasValue && player.MultiKills != null && player.MultiKills.ContainsKey(5))
                        .Select(player => new { match.MatchId, match.StartTime, HeroId = player.HeroId!.Value }))
                    .ToList();

                int totalRampageMatches = rampageEntries.Select(e => e.MatchId).Distinct().Count();
                writer.WriteLine($"Player {steamName} has got {totalRampageMatches} total Rampages\n");

                var groupedRampages = rampageEntries
                    .GroupBy(x => x.HeroId)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(x => new { x.MatchId, x.StartTime })
                              .OrderByDescending(m => m.StartTime ?? 0)
                              .ToList()
                    );

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

                        writer.WriteLine("| Match ID | Date |");
                        writer.WriteLine("|----------|------|");

                        foreach (var match in group.Value)
                        {
                            string dateStr = match.StartTime.HasValue
                                ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).ToLocalTime().ToString("yyyy-MM-dd")
                                : "-";
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{match.MatchId}) | {dateStr} |");
                        }

                        writer.WriteLine();
                    }
                    else
                    {
                        writer.WriteLine($"### Hero ID {heroId} (Name not found)");
                        writer.WriteLine("| Match ID | Date |");
                        writer.WriteLine("|----------|------|");

                        foreach (var match in group.Value)
                        {
                            string dateStr = match.StartTime.HasValue
                                ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).ToLocalTime().ToString("yyyy-MM-dd")
                                : "-";
                            writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{match.MatchId}) | {dateStr} |");
                        }

                        writer.WriteLine();
                    }
                }
            }
        }

       
        public static void GenerateMainReadme(Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)> steamProfiles, Dictionary<int, string> gameModes, Dictionary<int, string> lobbyTypes, Dictionary<int, string> patches)
        {
            string repoRoot = ResolveRepoRoot();
            string filePath = Path.Combine(repoRoot, "README.md");

            Console.WriteLine($"Generating main README at: {filePath}");

            using (var writer = new StreamWriter(filePath))
            {
                writer.WriteLine("# Dota 2 Rampage Tracker");
                writer.WriteLine("This repository contains rampage tracking data for various Dota 2 players.\n");
                writer.WriteLine($"> Last updated: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC\n");

                writer.WriteLine("## Players");
                writer.WriteLine("| Player Name | Profile Picture | Rampages | Rampage Rate | Win Rate (Total) | Win Rate (Unranked) | Win Rate (Ranked) | Rampage File |");
                writer.WriteLine("|-------------|-----------------|----------|--------------|------------------|---------------------|-------------------|--------------|");

                foreach (var steamProfile in steamProfiles
                    .OrderByDescending(kv => kv.Value.Totals.ContainsKey("rampages") ? kv.Value.Totals["rampages"] : 0))
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
                    double rampageRate = totalMatches > 0 ? (double)rampagesTotal / totalMatches * 100 : 0;
                    // Always use a repo-relative path for links so they work on GitHub and locally
                    string rampageRelativePath = $"Players/{playerId}/Rampages.md";
                    writer.WriteLine($"| {playerName} | ![Profile Picture]({avatarUrl}) | {rampagesTotal} | {rampageRate.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}% | {winRateTotal.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}% | {winRateUnranked.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}% | {winRateRanked.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}% | [Rampages](./{rampageRelativePath}) |");
                }
            }

            Console.WriteLine("Main README generated successfully.");
        }

        private static string ResolveRepoRoot()
        {
            // Walk up from CWD to locate the repo root (directory containing .git)
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                {
                    return dir.FullName;
                }
                dir = dir.Parent!;
            }
            // Fallback to CWD if .git not found
            return Directory.GetCurrentDirectory();
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