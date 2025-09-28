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
        // Config knobs
    public static int DefaultParseWorkers = 32;                 // background parse workers (bounded to avoid stampede)
        public static int ParsePollDelaySeconds = 30;               // delay between poll attempts
        public static int ParseAttemptsPerWorker = 2;               // per-run polling attempts
        public static int MaxParseRequestsTotal = 15;               // cutoff across runs
        public static int ProgressLogEvery = 50;                    // progress log cadence
        public static string PendingParsesFileName = "PendingParses.json";
        public static string PendingAttemptsFileName = "PendingAttempts.json";
    public static string PendingMetaFileName = "PendingMeta.json"; // passive mode: last status check timestamps

    // Passive scan tuning (to avoid blasting requests)
    public static int PassiveCheckPerPlayerPerCycle = 500;      // max status/details checks per player per minute in passive mode
    public static int PassiveStatusCooldownSeconds = 50;       // min seconds between checks for the same match in passive mode
        public static bool VerboseLogging = true;                   // toggle extra logs
    public static int PendingProgressEvery = 500;               // progress log cadence during pending scans
    public static int PendingScanParallelism = 24;             // bounded parallelism for pending scans

        // State
        private static readonly string errorLogFilePath = Path.Combine(Program.outputDirectory, $"{DateTime.UtcNow:HH_mm_ss}_error_log.txt");
        private static readonly ConcurrentDictionary<long, bool> parseRequested = new();
        private static readonly ConcurrentDictionary<long, Match?> detailsCache = new();
        private static readonly object errorLogLock = new();
        private static readonly object pendingWriteLock = new();
        private static readonly object cacheWriteLock = new();

        // Metrics (atomic)
        private static long metricParseRequests = 0;
        private static long metricRecovered = 0;
        private static long metricDropped = 0;
        private static long metricPollChecks = 0;
    private static long metricEnqueued = 0;
    private static long metricResolvedViaStatus = 0;
    private static long metricResolvedViaDetails = 0;

        // Background queue
        private class ParseJob { public long PlayerId; public long MatchId; }
        private static readonly ConcurrentQueue<ParseJob> parseQueue = new();
        private static readonly SemaphoreSlim parseSignal = new(0);
        private static CancellationTokenSource? parseCts;
        private static Task[]? parseWorkers;

        static MatchProcessor()
        {
            try
            {
                var dir = Path.GetDirectoryName(errorLogFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (!File.Exists(errorLogFilePath)) File.Create(errorLogFilePath).Dispose();
            }
            catch { }
        }

        public static void StartParseBackground(HttpClient client, int workers = -1)
        {
            StopParseBackground();
            parseCts = new CancellationTokenSource();
            int w = workers > 0 ? workers : DefaultParseWorkers;
            parseWorkers = Enumerable.Range(0, Math.Max(1, w))
                .Select(_ => Task.Run(() => ParseWorkerLoop(client, parseCts.Token)))
                .ToArray();
        }

        public static void StopParseBackground()
        {
            try { parseCts?.Cancel(); } catch { }
            var count = parseWorkers?.Length ?? 0;
            for (int i = 0; i < count; i++) { try { parseSignal.Release(); } catch { } }
            if (parseWorkers != null)
            {
                try { Task.WaitAll(parseWorkers, TimeSpan.FromSeconds(2)); } catch { }
            }
            parseWorkers = null;
            parseCts?.Dispose();
        }

        public static void EnqueueParse(long playerId, long matchId)
        {
            parseQueue.Enqueue(new ParseJob { PlayerId = playerId, MatchId = matchId });
            try { parseSignal.Release(); } catch { }
            Interlocked.Increment(ref metricEnqueued);
            ProgressTracker.IncEnqueued(playerId);
            if (VerboseLogging)
            {
                Console.WriteLine($"[enqueue] player={playerId} match={matchId}");
            }
        }

        private static async Task ParseWorkerLoop(HttpClient client, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try { await parseSignal.WaitAsync(ct); } catch { if (ct.IsCancellationRequested) break; }
                if (!parseQueue.TryDequeue(out var job)) continue;
                try { await ProcessParseJob(client, job.PlayerId, job.MatchId, ct); } catch { }
            }
        }

        private static async Task ProcessParseJob(HttpClient client, long playerId, long matchId, CancellationToken ct)
        {
            var existing = await FetchMatchDetailsOnce(client, matchId);
            detailsCache[matchId] = existing;
            if (HasParsedDataForPlayer(existing, playerId))
            {
                if (existing?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                {
                    lock (cacheWriteLock)
                    {
                        SaveRampageMatchesToCache(playerId.ToString(), new List<Match> { existing! });
                    }
                }
                await RemovePendingParse(playerId.ToString(), matchId);
                RemoveAttempt(playerId.ToString(), matchId);
                Interlocked.Increment(ref metricRecovered);
                Interlocked.Increment(ref metricResolvedViaDetails);
                ProgressTracker.IncResolvedDetails(playerId);
                if (VerboseLogging)
                {
                    Console.WriteLine($"[resolved:details-first] player={playerId} match={matchId}");
                }
                return;
            }

            var totalAttempts = GetAttempts(playerId.ToString(), matchId);
            if (totalAttempts >= MaxParseRequestsTotal)
            {
                await RemovePendingParse(playerId.ToString(), matchId);
                RemoveAttempt(playerId.ToString(), matchId);
                Interlocked.Increment(ref metricDropped);
                Console.WriteLine($"Dropping match {matchId} for player {playerId} after {totalAttempts} total parse requests.");
                return;
            }

            bool requestedOnce = false;
            for (int i = 1; i <= ParseAttemptsPerWorker && !ct.IsCancellationRequested; i++)
            {
                if (!requestedOnce)
                {
                    await RateLimiter.EnsureRateLimit();
                    _ = RequestMatchParse(client, matchId);
                    Interlocked.Increment(ref metricParseRequests);
                    var afterInc = IncrementAttempts(playerId.ToString(), matchId);
                    await AppendPendingParse(playerId.ToString(), matchId);
                    requestedOnce = true;
                    if (VerboseLogging)
                    {
                        Console.WriteLine($"[parse-requested] player={playerId} match={matchId} attempts={afterInc}/{MaxParseRequestsTotal}");
                    }
                    if (afterInc >= MaxParseRequestsTotal)
                    {
                        await RemovePendingParse(playerId.ToString(), matchId);
                        RemoveAttempt(playerId.ToString(), matchId);
                        Interlocked.Increment(ref metricDropped);
                        Console.WriteLine($"Dropping match {matchId} for player {playerId} after {afterInc} total parse requests.");
                        return;
                    }
                }

                try { await Task.Delay(TimeSpan.FromSeconds(ParsePollDelaySeconds), ct); } catch { }

                var after = await FetchMatchDetailsOnce(client, matchId);
                detailsCache[matchId] = after;
                Interlocked.Increment(ref metricPollChecks);
                // New: if the parse status says has_parsed=true, treat as resolved and remove from pending
                var hasParsed = await GetRequestHasParsed(client, matchId);
                if (hasParsed == true)
                {
                    // Try to update rampage cache if data is present; otherwise just remove from pending as requested
                    if (!HasParsedDataForPlayer(after, playerId))
                    {
                        // Attempt one more fetch now that parse is reported as done
                        after = await FetchMatchDetailsOnce(client, matchId);
                        detailsCache[matchId] = after;
                    }
                    if (HasParsedDataForPlayer(after, playerId) && after?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                    {
                        lock (cacheWriteLock)
                        {
                            SaveRampageMatchesToCache(playerId.ToString(), new List<Match> { after! });
                        }
                    }
                    await RemovePendingParse(playerId.ToString(), matchId);
                    RemoveAttempt(playerId.ToString(), matchId);
                    Interlocked.Increment(ref metricRecovered);
                    Interlocked.Increment(ref metricResolvedViaStatus);
                        ProgressTracker.IncResolvedStatus(playerId);
                    if (VerboseLogging)
                    {
                        Console.WriteLine($"[resolved:status] player={playerId} match={matchId}");
                    }
                    return;
                }
                if (HasParsedDataForPlayer(after, playerId))
                {
                    if (after?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                    {
                        lock (cacheWriteLock)
                        {
                            SaveRampageMatchesToCache(playerId.ToString(), new List<Match> { after! });
                        }
                    }
                    await RemovePendingParse(playerId.ToString(), matchId);
                    RemoveAttempt(playerId.ToString(), matchId);
                    Interlocked.Increment(ref metricRecovered);
                    Interlocked.Increment(ref metricResolvedViaDetails);
                        ProgressTracker.IncResolvedDetails(playerId);
                    if (VerboseLogging)
                    {
                        Console.WriteLine($"[resolved:details] player={playerId} match={matchId}");
                    }
                    return;
                }
            }
            // still unresolved -> remains pending
            if (VerboseLogging)
            {
                Console.WriteLine($"[pending-continue] player={playerId} match={matchId} after {ParseAttemptsPerWorker} polls");
            }
        }

        public static async Task<List<Match>> GetRampageMatches(HttpClient client, long playerId, List<Match> matches, bool updateWatermark = true)
        {
            var rampageMatches = new ConcurrentBag<Match>();
            int totalMatches = matches.Count;
            int processedMatches = 0;

            var recoveredFromPending = await ProcessPendingParses(client, playerId);
            foreach (var r in recoveredFromPending) rampageMatches.Add(r);

            foreach (var match in matches)
            {
                long matchId = match.MatchId;
                if (detailsCache.TryGetValue(matchId, out var cached) && HasParsedDataForPlayer(cached, playerId))
                {
                    if (cached?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                    {
                        rampageMatches.Add(cached!);
                    }
                }
                else
                {
                    EnqueueParse(playerId, matchId);
                }

                int done = Interlocked.Increment(ref processedMatches);
                DisplayProgress(done, totalMatches);
            }

            if (matches.Any() && updateWatermark)
            {
                long newWatermark = matches.Last().MatchId;
                UpdateLastCheckedMatchId(playerId.ToString(), newWatermark);
                SaveRampageMatchesToCache(playerId.ToString(), rampageMatches.ToList());
            }

            return rampageMatches.ToList();
        }

        private static void DisplayProgress(int processed, int total)
        {
            if (total <= 0) return;
            if (processed % ProgressLogEvery != 0 && processed != total) return;
            TimeSpan timeSpent = Program.stopwatch.Elapsed;
            Console.Write($"\rProcessing matches: {processed}/{total} ({(processed * 100) / Math.Max(1, total)}%) - Time spent: {timeSpent:hh\\:mm\\:ss}");
        }

        public static async Task<IEnumerable<Match>> GetPlayerMatches(HttpClient client, long playerId, long lastCheckedMatchId, int maxPages = 20)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/players/{playerId}/matches?project=match_id";

            List<Match> list;
            try
            {
                var response = await ApiHelper.GetStringWithBackoff(client, url);
                list = JsonConvert.DeserializeObject<List<Match>>(response) ?? new List<Match>();
            }
            catch (ApiHelper.CircuitOpenException)
            {
                return new List<Match>();
            }

            var filtered = list
                .Where(m => m.MatchId > lastCheckedMatchId)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .OrderBy(m => m.MatchId)
                .ToList();

            Console.WriteLine($"Player {playerId}: collected {filtered.Count} new matches.");
            return filtered;
        }

        private static async Task<bool> RequestMatchParse(HttpClient client, long matchId)
        {
            string url = $"https://api.opendota.com/api/request/{matchId}";
            try
            {
                var response = await ApiHelper.PostWithBackoff(client, url, null);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                LogError(matchId, $"Parse request failed: {ex.Message}");
                return false;
            }
        }

        private static async Task<Match?> FetchMatchDetailsOnce(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/matches/{matchId}";
            try
            {
                var response = await ApiHelper.GetAsyncWithBackoff(client, url);
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests) return null;
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync();
                return JsonConvert.DeserializeObject<Match>(body);
            }
            catch (Exception ex)
            {
                LogError(matchId, ex.Message);
                return null;
            }
        }

        private static bool HasParsedDataForPlayer(Match? match, long playerId)
        {
            if (match == null || match.Players == null) return false;
            var p = match.Players.FirstOrDefault(x => x.AccountId == (int)playerId);
            return p != null && p.MultiKills != null;
        }

        private static string PendingPath(string playerID)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            return Path.Combine(playerDirectory, PendingParsesFileName);
        }

        private static async Task<List<long>> LoadPendingParses(string playerID)
        {
            var path = PendingPath(playerID);
            if (!File.Exists(path)) return new List<long>();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
            }
            catch { return new List<long>(); }
        }

        // Public snapshot helpers for stats/summary views
        public static List<long> GetPendingList(long playerId)
        {
            try
            {
                var path = PendingPath(playerId.ToString());
                if (!File.Exists(path)) return new List<long>();
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
            }
            catch { return new List<long>(); }
        }

        private static async Task SavePendingParses(string playerID, List<long> ids)
        {
            var path = PendingPath(playerID);
            var json = JsonConvert.SerializeObject(ids.Distinct().OrderBy(x => x), Formatting.Indented);
            lock (pendingWriteLock)
            {
                File.WriteAllText(path, json);
            }
            await Task.CompletedTask;
        }

        private static async Task AppendPendingParse(string playerID, long matchId)
        {
            // Make read-modify-write atomic to avoid races under parallel scans
            lock (pendingWriteLock)
            {
                var path = PendingPath(playerID);
                List<long> list;
                if (File.Exists(path))
                {
                    try { list = JsonConvert.DeserializeObject<List<long>>(File.ReadAllText(path)) ?? new List<long>(); }
                    catch { list = new List<long>(); }
                }
                else
                {
                    list = new List<long>();
                }
                if (!list.Contains(matchId))
                {
                    list.Add(matchId);
                    var json = JsonConvert.SerializeObject(list.Distinct().OrderBy(x => x), Formatting.Indented);
                    File.WriteAllText(path, json);
                }
            }
            await Task.CompletedTask;
        }

        private static async Task RemovePendingParse(string playerID, long matchId)
        {
            // Make read-modify-write atomic to avoid races under parallel scans
            lock (pendingWriteLock)
            {
                var path = PendingPath(playerID);
                List<long> list;
                if (File.Exists(path))
                {
                    try { list = JsonConvert.DeserializeObject<List<long>>(File.ReadAllText(path)) ?? new List<long>(); }
                    catch { list = new List<long>(); }
                }
                else
                {
                    list = new List<long>();
                }
                if (list.Remove(matchId))
                {
                    var json = JsonConvert.SerializeObject(list.Distinct().OrderBy(x => x), Formatting.Indented);
                    File.WriteAllText(path, json);
                }
            }
            await Task.CompletedTask;
        }

        private static string AttemptsPath(string playerID)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            return Path.Combine(playerDirectory, PendingAttemptsFileName);
        }

        private static string PendingMetaPath(string playerID)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            return Path.Combine(playerDirectory, PendingMetaFileName);
        }

        private static Dictionary<long, int> LoadAttempts(string playerID)
        {
            var path = AttemptsPath(playerID);
            try
            {
                if (!File.Exists(path)) return new Dictionary<long, int>();
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<Dictionary<long, int>>(json) ?? new Dictionary<long, int>();
            }
            catch { return new Dictionary<long, int>(); }
        }

        public static Dictionary<long, int> GetAttemptsSnapshot(long playerId)
        {
            // Safe read of attempts for stats
            return LoadAttempts(playerId.ToString());
        }

        private static void SaveAttempts(string playerID, Dictionary<long, int> data)
        {
            var path = AttemptsPath(playerID);
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            lock (pendingWriteLock)
            {
                File.WriteAllText(path, json);
            }
        }

        private static int GetAttempts(string playerID, long matchId)
        {
            var map = LoadAttempts(playerID);
            return map.TryGetValue(matchId, out var n) ? n : 0;
        }

        private static int IncrementAttempts(string playerID, long matchId)
        {
            lock (pendingWriteLock)
            {
                var map = LoadAttempts(playerID);
                map.TryGetValue(matchId, out var n);
                map[matchId] = n + 1;
                SaveAttempts(playerID, map);
                return n + 1;
            }
        }

        private static void RemoveAttempt(string playerID, long matchId)
        {
            lock (pendingWriteLock)
            {
                var map = LoadAttempts(playerID);
                if (map.Remove(matchId)) SaveAttempts(playerID, map);
            }
        }

        private static Dictionary<long, long> LoadPendingMeta(string playerID)
        {
            var path = PendingMetaPath(playerID);
            try
            {
                if (!File.Exists(path)) return new Dictionary<long, long>();
                var json = File.ReadAllText(path);
                return JsonConvert.DeserializeObject<Dictionary<long, long>>(json) ?? new Dictionary<long, long>();
            }
            catch { return new Dictionary<long, long>(); }
        }

        public static Dictionary<long, long> GetPendingMetaSnapshot(long playerId)
        {
            // Expose last-checked timestamps for pending items (if any)
            return LoadPendingMeta(playerId.ToString());
        }

        private static void SavePendingMeta(string playerID, Dictionary<long, long> data)
        {
            var path = PendingMetaPath(playerID);
            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            lock (pendingWriteLock)
            {
                File.WriteAllText(path, json);
            }
        }

        public static async Task<List<Match>> ProcessPendingParses(HttpClient client, long playerId, bool passive = false, bool scanAll = false)
        {
            var recoveredBag = new ConcurrentBag<Match>();
            var pending = await LoadPendingParses(playerId.ToString());
            if (!pending.Any()) return new List<Match>();

            int still = 0;
            Dictionary<long, long>? meta = null;
            long nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int cooldown = PassiveStatusCooldownSeconds;

            IEnumerable<long> iterateIds;
            if (passive && !scanAll)
            {
                meta = LoadPendingMeta(playerId.ToString());
                // Order by oldest last-checked first; entries without meta first (ts=0)
                var ordered = pending.OrderBy(mid => meta!.TryGetValue(mid, out var ts) ? ts : 0L).ToList();
                int budget = Math.Max(1, PassiveCheckPerPlayerPerCycle);
                int used = 0;
                var picked = new List<long>(budget);
                foreach (var mid in ordered)
                {
                    if (used >= budget) break;
                    if (meta.TryGetValue(mid, out var lastTs) && (nowEpoch - lastTs) < cooldown)
                    {
                        continue; // skip this cycle due to cooldown
                    }
                    picked.Add(mid);
                    used++;
                }
                iterateIds = picked;
            }
            else
            {
                iterateIds = pending;
            }

            // Materialize iteration set and initialize progress counters for passive scans
            var iterateList = iterateIds.ToList();
            int totalLoop = iterateList.Count;
            int processedLoop = 0;
            if (passive)
            {
                Logger.Info("pending scan start", ctx: new Dictionary<string, object?> { {"player", playerId }, {"count", totalLoop }, {"scanAll", scanAll } });
            }

            bool metaChanged = false;
            object metaLock = new object();
            var sem = new SemaphoreSlim(Math.Max(1, PendingScanParallelism));
            var tasks = new List<Task>(totalLoop);
            foreach (var mid in iterateList)
            {
                await sem.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var m = await FetchMatchDetailsOnce(client, mid);
                        detailsCache[mid] = m;
                        if (HasParsedDataForPlayer(m, playerId))
                        {
                            if (m?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                            {
                                recoveredBag.Add(m!);
                            }
                            await RemovePendingParse(playerId.ToString(), mid);
                            RemoveAttempt(playerId.ToString(), mid);
                            Interlocked.Increment(ref metricRecovered);
                            Interlocked.Increment(ref metricResolvedViaDetails);
                            ProgressTracker.IncResolvedDetails(playerId);
                            if (VerboseLogging)
                            {
                                Console.WriteLine($"[pending:resolved-details] player={playerId} match={mid}");
                            }
                        }
                        else
                        {
                            // New: check parse status endpoint and remove if has_parsed=true
                            var hasParsed = await GetRequestHasParsed(client, mid);
                            if (hasParsed == true)
                            {
                                // Try to fetch details to include rampage if available
                                if (!HasParsedDataForPlayer(m, playerId))
                                {
                                    m = await FetchMatchDetailsOnce(client, mid);
                                    detailsCache[mid] = m;
                                }
                                if (HasParsedDataForPlayer(m, playerId) && m?.Players?.FirstOrDefault(x => x.AccountId == (int)playerId)?.MultiKills?.ContainsKey(5) == true)
                                {
                                    recoveredBag.Add(m!);
                                }
                                await RemovePendingParse(playerId.ToString(), mid);
                                RemoveAttempt(playerId.ToString(), mid);
                                Interlocked.Increment(ref metricRecovered);
                                Interlocked.Increment(ref metricResolvedViaStatus);
                                ProgressTracker.IncResolvedStatus(playerId);
                                if (VerboseLogging)
                                {
                                    Console.WriteLine($"[pending:resolved-status] player={playerId} match={mid}");
                                }
                                if (meta != null)
                                {
                                    lock (metaLock)
                                    {
                                        meta.Remove(mid);
                                        metaChanged = true;
                                    }
                                }
                            }
                            else if (!passive)
                            {
                                var attemptsSoFar = GetAttempts(playerId.ToString(), mid);
                                if (attemptsSoFar >= MaxParseRequestsTotal)
                                {
                                    await RemovePendingParse(playerId.ToString(), mid);
                                    RemoveAttempt(playerId.ToString(), mid);
                                    Interlocked.Increment(ref metricDropped);
                                    Console.WriteLine($"Dropping match {mid} for player {playerId} after {attemptsSoFar} parse requests.");
                                }
                                else
                                {
                                    EnqueueParse(playerId, mid);
                                    Interlocked.Increment(ref still);
                                }
                            }
                            else if (!scanAll)
                            {
                                // passive: just stamp last-checked to avoid re-checking too frequently
                                if (meta != null)
                                {
                                    lock (metaLock)
                                    {
                                        meta[mid] = nowEpoch;
                                        metaChanged = true;
                                    }
                                }
                            }
                        }
                    }
                    finally
                    {
                        var done = Interlocked.Increment(ref processedLoop);
                        if (passive && (done % Math.Max(1, PendingProgressEvery) == 0))
                        {
                            Logger.Info("pending scan progress", ctx: new Dictionary<string, object?> { {"player", playerId }, {"processed", done }, {"total", totalLoop }, {"recoveredSoFar", recoveredBag.Count } });
                        }
                        sem.Release();
                    }
                }));
            }

            await Task.WhenAll(tasks);

            if (meta != null && metaChanged)
            {
                SavePendingMeta(playerId.ToString(), meta);
            }

            if (!passive && still > 0)
            {
                Console.WriteLine($"Player {playerId}: {still} matches (re)queued for background parse.");
            }
            if (passive)
            {
                Logger.Info("pending scan done", ctx: new Dictionary<string, object?> { {"player", playerId }, {"processed", processedLoop }, {"total", totalLoop }, {"recovered", recoveredBag.Count } });
            }
            return recoveredBag.ToList();
        }

        private class RequestStatus
        {
            [JsonProperty("has_parsed")] public bool HasParsed { get; set; }
        }

        private static async Task<bool?> GetRequestHasParsed(HttpClient client, long matchId)
        {
            await RateLimiter.EnsureRateLimit();
            string url = $"https://api.opendota.com/api/request/{matchId}";
            try
            {
                var resp = await ApiHelper.GetAsyncWithBackoff(client, url);
                if (!resp.IsSuccessStatusCode) return null;
                var body = await resp.Content.ReadAsStringAsync();
                var status = JsonConvert.DeserializeObject<RequestStatus>(body);
                return status?.HasParsed;
            }
            catch
            {
                return null;
            }
        }

        public static (int queued, int workers) ParseQueueSnapshot()
        {
            return (parseQueue.Count, parseWorkers?.Length ?? 0);
        }

        private static void LogError(long matchId, string errorMessage)
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            string matchUrl = $"https://www.opendota.com/matches/{matchId}";
            string logMessage = $"Error - {timestamp} - {matchUrl} - {errorMessage}";
            lock (errorLogLock)
            {
                try { File.AppendAllText(errorLogFilePath, logMessage + Environment.NewLine); } catch { }
            }
        }

        public static long ReadLastCheckedMatchId(string playerID)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            string lastCheckedMatchFile = Path.Combine(playerDirectory, "LastCheckedMatch.txt");
            if (File.Exists(lastCheckedMatchFile))
            {
                try
                {
                    var lines = File.ReadAllLines(lastCheckedMatchFile);
                    if (lines.Length > 0) return long.Parse(lines[0]);
                }
                catch { }
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

            List<Match> cachedRampageMatches = new List<Match>();
            if (File.Exists(cacheFilePath))
            {
                try
                {
                    var jsonData = File.ReadAllText(cacheFilePath);
                    cachedRampageMatches = JsonConvert.DeserializeObject<List<Match>>(jsonData) ?? new List<Match>();
                }
                catch { }
            }

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

            foreach (var matchId in missing)
            {
                await RateLimiter.EnsureRateLimit();
                var details = await FetchMatchDetailsOnce(client, matchId);
                if (details != null && details.StartTime.HasValue)
                {
                    foreach (var m in list.Where(x => x.MatchId == matchId))
                    {
                        m.StartTime = details.StartTime;
                    }
                }
            }
            return list;
        }

        public static void WriteRampageCache(string playerID, List<Match> allRampageMatches)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerID);
            Directory.CreateDirectory(playerDirectory);
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");
            File.WriteAllText(cacheFilePath, JsonConvert.SerializeObject(allRampageMatches, Formatting.Indented));
        }

        public static (long parseRequests, long recovered, long dropped, long pollChecks) MetricsSnapshot()
        {
            return (
                Interlocked.Read(ref metricParseRequests),
                Interlocked.Read(ref metricRecovered),
                Interlocked.Read(ref metricDropped),
                Interlocked.Read(ref metricPollChecks)
            );
        }

        public static (long parseRequests, long recovered, long dropped, long pollChecks, long enqueued, long resolvedViaStatus, long resolvedViaDetails) MetricsDetailed()
        {
            return (
                Interlocked.Read(ref metricParseRequests),
                Interlocked.Read(ref metricRecovered),
                Interlocked.Read(ref metricDropped),
                Interlocked.Read(ref metricPollChecks),
                Interlocked.Read(ref metricEnqueued),
                Interlocked.Read(ref metricResolvedViaStatus),
                Interlocked.Read(ref metricResolvedViaDetails)
            );
        }

        public static void MetricsReset()
        {
            Interlocked.Exchange(ref metricParseRequests, 0);
            Interlocked.Exchange(ref metricRecovered, 0);
            Interlocked.Exchange(ref metricDropped, 0);
            Interlocked.Exchange(ref metricPollChecks, 0);
            Interlocked.Exchange(ref metricEnqueued, 0);
            Interlocked.Exchange(ref metricResolvedViaStatus, 0);
            Interlocked.Exchange(ref metricResolvedViaDetails, 0);
        }

        public static int GetPendingCount(long playerId)
        {
            try
            {
                var path = PendingPath(playerId.ToString());
                if (!File.Exists(path)) return 0;
                var json = File.ReadAllText(path);
                var list = JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
                return list.Count;
            }
            catch { return 0; }
        }

        public static async Task<List<long>> RevalidateCachedRampages(HttpClient client, long playerId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerId.ToString());
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");
            var unavailable = new List<long>();
            if (!File.Exists(cacheFilePath)) return unavailable;

            List<Match> cached;
            try
            {
                var json = await File.ReadAllTextAsync(cacheFilePath);
                cached = JsonConvert.DeserializeObject<List<Match>>(json) ?? new List<Match>();
            }
            catch
            {
                return unavailable;
            }

            foreach (var m in cached)
            {
                await RateLimiter.EnsureRateLimit();
                var url = $"https://api.opendota.com/api/matches/{m.MatchId}";
                try
                {
                    var resp = await ApiHelper.GetAsyncWithBackoff(client, url);
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                    {
                        var body = await resp.Content.ReadAsStringAsync();
                        var full = JsonConvert.DeserializeObject<Match>(body);
                        var hasRampage = full?.Players != null && full.Players.Any(p => p.AccountId == (int)playerId && p.MultiKills != null && p.MultiKills.ContainsKey(5));
                        if (!hasRampage)
                        {
                            if (parseRequested.TryAdd(m.MatchId, true))
                            {
                                _ = RequestMatchParse(client, m.MatchId);
                                Interlocked.Increment(ref metricParseRequests);
                            }
                            unavailable.Add(m.MatchId);
                        }
                    }
                    else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound || (int)resp.StatusCode >= 500)
                    {
                        if (parseRequested.TryAdd(m.MatchId, true))
                        {
                            _ = RequestMatchParse(client, m.MatchId);
                            Interlocked.Increment(ref metricParseRequests);
                        }
                        unavailable.Add(m.MatchId);
                    }
                }
                catch
                {
                    // ignore and continue
                }
            }

            if (unavailable.Count > 0)
            {
                try
                {
                    Directory.CreateDirectory(playerDirectory);
                    var report = Path.Combine(playerDirectory, "UnavailableMatches.txt");
                    var lines = new List<string>();
                    lines.Add($"Timestamp UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
                    lines.Add($"Player {playerId}: {unavailable.Count} rampage matches currently unavailable on OpenDota (parse requested if possible):");
                    lines.AddRange(unavailable.Select(id => $"https://www.opendota.com/matches/{id}"));
                    await File.WriteAllLinesAsync(report, lines);
                }
                catch { }
            }

            return unavailable;
        }

        public static async Task<int> ForceParseCachedRampages(HttpClient client, long playerId)
        {
            string playerDirectory = Path.Combine(Program.outputDirectory, playerId.ToString());
            string cacheFilePath = Path.Combine(playerDirectory, "RampageMatchesCache.json");
            if (!File.Exists(cacheFilePath)) return 0;

            var json = await File.ReadAllTextAsync(cacheFilePath);
            var cached = JsonConvert.DeserializeObject<List<Match>>(json) ?? new List<Match>();
            int submitted = 0;
            foreach (var m in cached)
            {
                await RateLimiter.EnsureRateLimit();
                if (parseRequested.TryAdd(m.MatchId, true))
                {
                    _ = RequestMatchParse(client, m.MatchId);
                    Interlocked.Increment(ref metricParseRequests);
                    submitted++;
                }
            }
            return submitted;
        }
    }
}