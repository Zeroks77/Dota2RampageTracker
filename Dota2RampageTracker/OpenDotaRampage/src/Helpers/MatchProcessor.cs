using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
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

        public static async Task<string> RequestMatchParsing(HttpClient client, long matchId)
        {
            string url = $"https://api.opendota.com/api/request/{matchId}{apiKey}";
            var response = await client.PostAsync(url, null);
            response.EnsureSuccessStatusCode();

            var jobResponse = JsonConvert.DeserializeObject<JobResponse>(await response.Content.ReadAsStringAsync());
            return jobResponse.Job.JobId;
        }

        public static async Task<bool> IsMatchParsed(HttpClient client, string jobId)
        {
            string url = $"https://api.opendota.com/api/request/{jobId}{apiKey}";
            try
            {
                var response = await client.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var jobStatus = JsonConvert.DeserializeObject<JobResponse>(await response.Content.ReadAsStringAsync());
                return response.StatusCode == System.Net.HttpStatusCode.OK;;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.BadRequest || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Log the error and return false to skip the match
                LogError(jobId, ex.Message);
                return false;
            }
        }

        public static async Task<List<Match>> GetRampageMatches(HttpClient client, long playerId, string steamName)
        {
            long lastCheckedMatchId = ReadLastCheckedMatchId(steamName);
            var matches = await GetPlayerMatches(client, playerId, lastCheckedMatchId);
            var rampageMatches = new ConcurrentBag<Match>();

            int totalMatches = matches.Count();
            int processedMatches = 0;

            var matchBatches = matches
                .Reverse() // Process from oldest to newest
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
                        var jobId = await RequestMatchParsing(client, matchId);
                        Console.WriteLine($"Requested parsing for match ID: {matchId}, Job ID: {jobId}");

                        // Wait until the match is parsed
                        while (!await IsMatchParsed(client, jobId))
                        {
                            Console.WriteLine($"Waiting for match ID: {matchId} to be parsed...");
                            await Task.Delay(10000); // Wait for 10 seconds before checking again
                        }

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
            var matches = JsonConvert.DeserializeObject<List<Match>>(response);

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
                LogError(matchId.ToString(), ex.Message);
                return null;
            }
        }

        private static void LogError(string jobId, string errorMessage)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string logMessage = $"Error - {timestamp} - Job ID: {jobId} - {errorMessage}";

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

        private static void SaveRampageMatchesToCache(string playerName, List<Match> newRampageMatches)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerName);
            Directory.CreateDirectory(playerDirectory);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");

            List<Match> cachedRampageMatches = new List<Match>();
            if (File.Exists(cacheFilePath))
            {
                var jsonData = File.ReadAllText(cacheFilePath);
                cachedRampageMatches = JsonConvert.DeserializeObject<List<Match>>(jsonData);
            }

            var allRampageMatches = cachedRampageMatches.Concat(newRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .ToList();

            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(allRampageMatches, Formatting.Indented));
        }
    }
}