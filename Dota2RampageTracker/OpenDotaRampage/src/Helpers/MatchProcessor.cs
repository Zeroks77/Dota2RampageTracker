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

        public static async Task<List<Match>> GetRampageMatches(HttpClient client, long playerId, List<Match> matches)
        {
            var rampageMatches = new ConcurrentBag<Match>();
            int totalMatches = matches.Count();
            int processedMatches = 0;

            var matchBatches = matches
                .Select((match, index) => new { match, index })
                .GroupBy(x => x.index / 500)
                .Select(g => g.Select(x => x.match).ToList())
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
                        Console.WriteLine($"Processed match {matchId}. Total processed: {processedMatches}/{totalMatches}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error {ex.Message}");
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
                UpdateLastCheckedMatchId(playerId.ToString(), matches.Last().MatchId);
                SaveRampageMatchesToCache(playerId.ToString(), rampageMatches.ToList());
            }

            return rampageMatches.ToList();
        }

        private static void DisplayProgress(int processed, int total)
        {
            TimeSpan timeSpent = Program.stopwatch.Elapsed;
            Console.Write($"\rProcessing matches: {processed}/{total} ({(processed * 100) / total}%) - Time spent: {timeSpent:hh\\:mm\\:ss}");
        }

        public static async Task<IEnumerable<Match>> GetPlayerMatches(HttpClient client, long playerId, long lastCheckedMatchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/players/{playerId}/matches";
            var response = await client.GetStringAsync(url);
            var matches = JsonConvert.DeserializeObject<List<Match>>(response);

            // Filter matches to only include those after the last checked match ID
            var newMatches = matches.Where(match => match.MatchId > lastCheckedMatchId).Reverse();

            return newMatches;
        }

        private static async Task<Match> GetMatchDetails(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/matches/{matchId}";
            if (RateLimiter.useApiKey)
            {
                url += $"?api_key={Program.apiKey}";
            }
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
                if (!response.IsSuccessStatusCode)
                {
                    return null;
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

        public static long ReadLastCheckedMatchId(string playerID)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
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

        private static void UpdateLastCheckedMatchId(string playerID, long matchId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            string lastCheckedMatchFile = Path.Combine(playerDirectory, "LastCheckedMatch.txt");

            File.WriteAllText(lastCheckedMatchFile, matchId.ToString());
        }

        private static void SaveRampageMatchesToCache(string playerID, List<Match> newRampageMatches)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");

            // Load existing rampage matches from cache
            List<Match> cachedRampageMatches = new List<Match>();
            if (File.Exists(cacheFilePath))
            {
                var jsonData = File.ReadAllText(cacheFilePath);
                cachedRampageMatches = JsonConvert.DeserializeObject<List<Match>>(jsonData);
            }

            // Combine new and cached rampage matches, ensuring distinct matches
            var allRampageMatches = cachedRampageMatches.Concat(newRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .Distinct()
                .ToList();

            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(allRampageMatches, Formatting.Indented));
        }
    }
}