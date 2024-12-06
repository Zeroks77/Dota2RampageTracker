using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OpenDotaRampage.Models;

namespace OpenDotaRampage.Helpers
{
    public static class MatchProcessor
    {
        private static readonly string errorLogFilePath = Path.Combine(Program.outputDirectory, $"{DateTime.UtcNow:HH_mm_ss}_error_log.txt");
        private static string apiKey;
        static MatchProcessor()
        {
            // Ensure the error log file is created
            if (!File.Exists(errorLogFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(errorLogFilePath));
                File.Create(errorLogFilePath).Dispose();
            }
            apiKey = "?api_key=" + Program.apiKey;
        }

        public static async Task RequestMatchParsing(HttpClient client, long matchId)
        {
            string url = $"https://api.opendota.com/api/request/{matchId}{apiKey}";
            var response = await client.PostAsync(url, null);
            response.EnsureSuccessStatusCode();
        }
        public static async Task<bool> IsMatchParsed(HttpClient client, long matchId)
        {
            string url = $"https://api.opendota.com/api/request/{matchId}{apiKey}";
            var response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var parsed = JsonConvert.DeserializeObject<bool>(await response.Content.ReadAsStringAsync());
            return parsed;
        }
        public static async Task<List<Match>> GetRampageMatches(HttpClient client, long playerId, string steamName)
        {
            long lastCheckedMatchId = ReadLastCheckedMatchId(steamName); // foreach (var match in matches)
            var matches = await GetPlayerMatches(client, playerId, lastCheckedMatchId);
            var parsedMatches = new List<Match>();
            // {
            //     await RequestMatchParsing(client, match.MatchId);
            //     Console.WriteLine($"Requested parsing for match ID: {match.MatchId}");

            //     // Wait until the match is parsed
            //     while (!await IsMatchParsed(client, match.MatchId))
            //     {
            //         Console.WriteLine($"Waiting for match ID: {match.MatchId} to be parsed...");
            //         await Task.Delay(10000); // Wait for 10 seconds before checking again
            //     }
            //     parsedMatches.Add(match);
            // }
            var rampageMatches = new ConcurrentBag<Match>();

            int totalMatches = matches.Count();
            int processedMatches = 0;

            var matchBatches = matches
                .Reverse() // Process from oldest to newest
                .Select((match, index) => new { match, index })
                .GroupBy(x => x.index / 500)
                .Select(g => g.Select(x => x.match).ToList())
                .Reverse()
                .ToList();

            foreach (var batch in matchBatches)
            {
                var tasks = batch.Select(async match =>
                {
                    await RateLimiter.EnsureRateLimit();
                    await RateLimiter.concurrencyLimiter.WaitAsync();
                    try
                    {
                        long matchId = match.MatchId;
                        var matchDetails = await GetMatchDetails(client, matchId);

                        if (matchDetails != null)
                        {
                            foreach (var player in matchDetails.Players)
                            {
                                if (player.AccountId == playerId && player.MultiKills != null && player.MultiKills.ContainsKey(5))
                                {
                                    rampageMatches.Add(matchDetails);
                                }
                            }
                        }
                        Interlocked.Increment(ref processedMatches);
                        DisplayProgress(processedMatches, totalMatches);
                    }
                    finally
                    {
                        RateLimiter.concurrencyLimiter.Release();
                    }
                });

                await Task.WhenAll(tasks);
            }

            if (matches.Any())
            {
                UpdateLastCheckedMatchId(steamName, matches.Last().MatchId);
                SaveRampageMatchesToCache(steamName, rampageMatches.ToList());
            }

            return rampageMatches.ToList();
        }


        public static async Task CheckSpecificMatch(HttpClient client, long playerId, long matchId)
        {
            var matchDetails = await GetMatchDetails(client, matchId);

            if (matchDetails != null)
            {
                foreach (var player in matchDetails.Players)
                {
                    if (player.AccountId == playerId)
                    {
                        Console.WriteLine($"Match ID: {matchDetails.MatchId}, Player ID: {player.AccountId}, Multikills: {string.Join(", ", player.MultiKills.Select(m => $"{m.Key}x: {m.Value}"))}");
                        return;
                    }
                }
                Console.WriteLine("Player not found in the specified match.");
            }
            else
            {
                Console.WriteLine("Match not found.");
            }
        }

        private static void DisplayProgress(int processed, int total)
        {
            TimeSpan timeSpent = Program.stopwatch.Elapsed;
            Console.Write($"\rProcessing matches: {processed}/{total} ({(processed * 100) / total}%) - Time spent: {timeSpent:hh\\:mm\\:ss}");
        }

        private static async Task<IEnumerable<Match>> GetPlayerMatches(HttpClient client, long playerId, long lastCheckedMatchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/players/{playerId}/matches";
            var response = await client.GetStringAsync(url);
            var matches =  JsonConvert.DeserializeObject<List<Match>>(response);

            // Filter matches to only include those after the last checked match ID
            var newMatches = matches.Where(match => match.MatchId > lastCheckedMatchId);

            return newMatches;
        }

        private static async Task<Match> GetMatchDetails(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/matches/{matchId}?api_key={Program.apiKey}";
            try
            {
                var response = await client.GetAsync(url);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Requeue the match and wait for 10 seconds
                    await Task.Delay(10000);
                    Console.WriteLine($"TooManyrequests. Delaying for 10 seconds.");
                    return await GetMatchDetails(client, matchId);
                }
                response.EnsureSuccessStatusCode();
                return JsonConvert.DeserializeObject<Match>(await response.Content.ReadAsStringAsync());
            }
            catch (Exception ex)
            {
                // Log errors to a separate log file
                LogError(matchId, ex.Message);
                return null;
            }
        }

        private static void LogError(long matchId, string errorMessage)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string matchUrl = $"https://www.opendota.com/matches/{matchId}";
            string logMessage = $"Error - {timestamp} - {matchUrl} - {errorMessage}";

            lock (errorLogFilePath)
            {
                File.AppendAllText(errorLogFilePath, logMessage + Environment.NewLine);
            }
        }

        private static long ReadLastCheckedMatchId(string playerName)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            string lastCheckedMatchFile = Path.Combine(playerDirectory, "LastCheckedMatch.txt");

            if (File.Exists(lastCheckedMatchFile))
            {
                var lines = File.ReadAllLines(lastCheckedMatchFile);
                if (lines.Length > 0)
                {
                    return long.Parse(lines[0]);
                }
            }

            return 0;
        }

        private static void UpdateLastCheckedMatchId(string playerName, long matchId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            Directory.CreateDirectory(playerDirectory);
            string lastCheckedMatchFile = Path.Combine(playerDirectory, "LastCheckedMatch.txt");

            File.WriteAllText(lastCheckedMatchFile, matchId.ToString());
        }

        private static void SaveRampageMatchesToCache(string playerName, List<Match> rampageMatches)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            Directory.CreateDirectory(playerDirectory);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");

            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(rampageMatches, Formatting.Indented));
        }
    }
}