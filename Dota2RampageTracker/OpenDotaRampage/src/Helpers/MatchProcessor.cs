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
        private static readonly ConcurrentDictionary<long, bool> parseRequested = new ConcurrentDictionary<long, bool>();

        static MatchProcessor()
        {
            // Ensure the error log file is created
            if (!File.Exists(errorLogFilePath))
            {
                var dir = Path.GetDirectoryName(errorLogFilePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.Create(errorLogFilePath).Dispose();
            }
        }

        public static async Task<List<Match>> GetRampageMatches(HttpClient client, long playerId, List<Match> matches)
        {
            var rampageMatches = new ConcurrentBag<Match>();
            int totalMatches = matches.Count;
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

                        if (matchDetails != null && matchDetails.Players != null)
                        {
                            foreach (var player in matchDetails.Players)
                            {
                                if (player.AccountId == playerId && player.MultiKills != null && player.MultiKills.ContainsKey(5))
                                {
                                    rampageMatches.Add(matchDetails);
                                    break;
                                }
                            }
                        }

                        int done = Interlocked.Increment(ref processedMatches);
                        DisplayProgress(done, totalMatches);
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
            if (total <= 0) return;
            if (processed % 50 != 0 && processed != total) return; // throttle console spam
            TimeSpan timeSpent = Program.stopwatch.Elapsed;
            Console.Write($"\rProcessing matches: {processed}/{total} ({(processed * 100) / Math.Max(1, total)}%) - Time spent: {timeSpent:hh\\:mm\\:ss}");
        }

        public static async Task<IEnumerable<Match>> GetPlayerMatches(HttpClient client, long playerId, long lastCheckedMatchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/players/{playerId}/matches";
            var response = await ApiHelper.GetStringWith429Retry(client, url);
            var matches = JsonConvert.DeserializeObject<List<Match>>(response) ?? new List<Match>();

            // Filter matches to only include those after the last checked match ID
            var newMatches = matches.Where(match => match.MatchId > lastCheckedMatchId).Reverse();

            return newMatches;
        }

        private static async Task<bool> RequestMatchParse(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/request/{matchId}";

            try
            {
                var response = await ApiHelper.PostWith429Retry(client, url, null);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                // Parser kann asynchron dauern. Nicht sofort pollen; Folgeläufe holen Details.
                return true;
            }
            catch (Exception ex)
            {
                LogError(matchId, $"Parse request failed: {ex.Message}");
            }
            return false;
        }

        private static async Task<Match?> GetMatchDetails(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();

            string url = $"https://api.opendota.com/api/matches/{matchId}";
            try
            {
                var response = await ApiHelper.GetAsyncWith429Retry(client, url);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // Requeue the match and wait briefly
                    await Task.Delay(3000);
                    Console.WriteLine($"Too many requests (429). Retrying in 3 seconds.");
                    return await GetMatchDetails(client, matchId);
                }
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync();
                var match = JsonConvert.DeserializeObject<Match>(body);

                // Wenn keine MultiKills vorhanden sind, einmalig Parse anstoßen und später erneut versuchen
                if (match != null && (match.Players == null || match.Players.All(p => p.MultiKills == null)))
                {
                    if (parseRequested.TryAdd(matchId, true))
                    {
                        _ = RequestMatchParse(client, matchId); // Fire-and-forget
                    }
                    return null; // späterer Durchlauf/Run holt die Daten
                }

                return match;
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
                cachedRampageMatches = JsonConvert.DeserializeObject<List<Match>>(jsonData) ?? new List<Match>();
            }

            // Combine new and cached rampage matches, preferring new (which likely contain StartTime)
            var allRampageMatches = newRampageMatches
                .Concat(cachedRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .ToList();

            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(allRampageMatches, Formatting.Indented));
        }

        public static async Task<List<Match>> EnrichStartTimes(HttpClient client, List<Match> matches)
        {
            var list = matches.ToList();
            var missing = list.Where(m => !m.StartTime.HasValue).Select(m => m.MatchId).Distinct().ToList();
            if (!missing.Any()) return list;

            var tasks = missing.Select(async matchId =>
            {
                await RateLimiter.EnsureRateLimit();
                await RateLimiter.concurrencyLimiter.WaitAsync();
                try
                {
                    var details = await GetMatchDetails(client, matchId);
                    if (details != null && details.StartTime.HasValue)
                    {
                        foreach (var m in list.Where(x => x.MatchId == matchId))
                        {
                            m.StartTime = details.StartTime;
                        }
                    }
                }
                finally
                {
                    RateLimiter.concurrencyLimiter.Release();
                }
            });

            await Task.WhenAll(tasks);
            return list;
        }

        public static void WriteRampageCache(string playerID, List<Match> allRampageMatches)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");
            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(allRampageMatches, Formatting.Indented));
        }
    }
}