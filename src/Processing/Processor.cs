using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RampageTracker.Core;
using RampageTracker.Data;
using RampageTracker.Models;
using RampageTracker.Rendering;

namespace RampageTracker.Processing
{
    public static class Processor
    {
    private static readonly System.Threading.SemaphoreSlim _rampageWrite = new(1, 1);
    private static readonly TimeSpan InitialDelay =
#if DEBUG
        TimeSpan.FromMilliseconds(50);
#else
        TimeSpan.FromMinutes(3);
#endif
    private const int InitialTries = 1;
        private const int MaxQueueTries = 15;

    public static async Task RunNewOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct, bool eagerPoll = false)
        {
            // 1) Load last-checked map per player
            var lastChecked = new Dictionary<long, long>();
            foreach (var pid in players)
            {
                lastChecked[pid] = await data.GetLastCheckedAsync(pid);
            }

            // 2) Single-request per player: fetch matches with a high limit (avoid pagination)
            const int BigLimit = 100000; // attempt to fetch all in one request; fallback to paging if server caps
            var playerMatches = new Dictionary<long, List<PlayerMatchSummary>>();
            var fetchSem = new System.Threading.SemaphoreSlim(Math.Max(1, Math.Min(workers, 8)));
            var fetchTasks = new List<Task>();
            Logger.Info("[new] Fetching matches (one request per player, high limit) ...");
            foreach (var pid in players)
            {
                await fetchSem.WaitAsync(ct);
                var pidLocal = pid;
                fetchTasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var ms = await api.GetPlayerMatchesAsync(pidLocal, limit: BigLimit, offset: 0) ?? Array.Empty<PlayerMatchSummary>();
                        var list = ms.ToList();
                        Logger.Info($"[new] Player {pidLocal}: fetched {ms.Length} matches (limit={BigLimit})");
                        // If we likely hit a cap (min match id still newer than lastchecked), fallback to paging
                        try
                        {
                            if (ms.Length >= BigLimit)
                            {
                                var minId = ms.Min(m => m.MatchId);
                                if (minId > lastChecked[pidLocal])
                                {
                                    Logger.Warn($"[new] Player {pidLocal}: limit {BigLimit} reached; continuing with pagination until lastchecked boundary...");
                                    var offset = ms.Length;
                                    while (!ct.IsCancellationRequested)
                                    {
                                        var page = await api.GetPlayerMatchesAsync(pidLocal, limit: 5000, offset: offset) ?? Array.Empty<PlayerMatchSummary>();
                                        if (page.Length == 0) break;
                                        list.AddRange(page);
                                        var pageMin = page.Min(m => m.MatchId);
                                        if (pageMin <= lastChecked[pidLocal]) break;
                                        offset += page.Length;
                                        if (offset > 200_000) { Logger.Warn($"[new] Player {pidLocal}: paging safety cap reached at offset {offset}"); break; }
                                    }
                                }
                            }
                        }
                        catch { }
                        lock (playerMatches) playerMatches[pidLocal] = list;
                    }
                    finally
                    {
                        try { fetchSem.Release(); } catch { }
                    }
                }, ct));
            }
            await Task.WhenAll(fetchTasks);

            // 3) Build a unique match map: matchId -> players (for whom this match is new)
            var matchToPlayers = new Dictionary<long, List<long>>();
            int totalNewPerPlayer = 0;
            foreach (var kv in playerMatches)
            {
                var pid = kv.Key;
                var last = lastChecked[pid];
                var list = kv.Value;
                var countNew = 0;
                foreach (var m in list)
                {
                    if (m.MatchId > last)
                    {
                        if (!matchToPlayers.TryGetValue(m.MatchId, out var plist))
                            matchToPlayers[m.MatchId] = plist = new List<long>(1);
                        plist.Add(pid);
                        countNew++;
                    }
                }
                totalNewPerPlayer += countNew;
                Logger.Info($"[new] Player {pid}: new matches since lastchecked: {countNew}");
            }
            var uniqueMatches = matchToPlayers.Keys.OrderBy(id => id).ToList();
            Logger.Info($"[new] Unique new matches across players (deduped): {uniqueMatches.Count} (raw sum per-player: {totalNewPerPlayer})");

            // 4) Process unique matches concurrently; avoid duplicate request/parse calls across players
            var throttler = new System.Threading.SemaphoreSlim(Math.Max(1, workers));
            var tasks = new List<Task>();
            var rampageAddsPerPlayer = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();

            foreach (var matchId in uniqueMatches)
            {
                await throttler.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        // Preferred flow: try GET /matches/{id} first
                        var match = await api.GetMatchAsync(matchId);
                        var gotParsedData = match?.Players != null;
                        if (gotParsedData)
                        {
                            // Summary across all players
                            try
                            {
                                IEnumerable<(long? AccountId, string? HeroName, int Count)> rampagers = match!.Players!
                                    .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                    .Select(p => (AccountId: (long?)p.AccountId, HeroName: (string?)Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p!.MultiKills![5]))
                                    .ToList();
                                Logger.LogMatchRampageSummary(match.MatchId, rampagers);
                            }
                            catch { }

                            foreach (var pid in matchToPlayers[matchId])
                            {
                                var mp = match!.Players!.FirstOrDefault(x => x.AccountId == (int)pid);
                                var cnt = 0; if (mp?.MultiKills != null) mp.MultiKills.TryGetValue(5, out cnt);
                                var heroId = mp?.HeroId; var heroName = Core.HeroCatalog.GetLocalizedName(heroId);
                                var isRampage = cnt > 0;
                                Logger.LogMatchEvaluation(pid, matchId, heroName, cnt, isRampage);
                                if (isRampage)
                                {
                                    var ramp = new RampageEntry
                                    {
                                        MatchId = matchId,
                                        HeroId = heroId,
                                        HeroName = heroName,
                                        StartTime = match.StartTime,
                                        MatchDate = match.StartTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).UtcDateTime : null
                                    };
                                    await data.AppendRampageAsync(pid, ramp);
                                    Logger.LogRampageFound(pid, matchId, heroName);
                                    rampageAddsPerPlayer.AddOrUpdate(pid, 1, (_, v) => v + 1);
                                }
                            }

                            // Update lastchecked per involved player
                            foreach (var pid in matchToPlayers[matchId])
                            {
                                await data.SetLastCheckedAsync(pid, matchId);
                            }
                        }
                        else
                        {
                            // Not parsed/available yet -> request parse and enqueue
                            long? jobId = null;
                            try { jobId = await api.RequestParseAsync(matchId); }
                            catch (Exception ex) { Logger.Warn($"[new] request parse failed for {matchId}: {ex.Message}"); }
                            if (jobId != null)
                            {
                                Logger.Info($"[new] Requested parse for match {matchId}, jobId={jobId}");
                            }
                            else
                            {
                                Logger.Warn($"[new] Request parse for match {matchId} returned no jobId");
                            }

                            // Optional quick poll if enabled
                            if (eagerPoll && jobId != null)
                            {
                                var quickDelay = TimeSpan.FromMilliseconds(25);
                                bool success = false;
                                try { success = await PollJobTwiceAsync(api, jobId.Value, quickDelay, ct); }
                                catch (Exception ex) { Logger.Warn($"[new] poll failed for job {jobId}: {ex.Message}"); }
                                if (success)
                                {
                                    // Fetch and process now
                                    try
                                    {
                                        var m2 = await api.GetMatchAsync(matchId);
                                        if (m2?.Players != null)
                                        {
                                            try
                                            {
                                                IEnumerable<(long? AccountId, string? HeroName, int Count)> rampagers = m2.Players
                                                    .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                                    .Select(p => (AccountId: (long?)p.AccountId, HeroName: (string?)Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p!.MultiKills![5]))
                                                    .ToList();
                                                Logger.LogMatchRampageSummary(m2.MatchId, rampagers);
                                            }
                                            catch { }

                                            foreach (var pid in matchToPlayers[matchId])
                                            {
                                                var mp = m2.Players.FirstOrDefault(x => x.AccountId == (int)pid);
                                                var cnt = 0; if (mp?.MultiKills != null) mp.MultiKills.TryGetValue(5, out cnt);
                                                var heroId = mp?.HeroId; var heroName = Core.HeroCatalog.GetLocalizedName(heroId);
                                                var isRampage = cnt > 0;
                                                Logger.LogMatchEvaluation(pid, matchId, heroName, cnt, isRampage);
                                                if (isRampage)
                                                {
                                                    var ramp = new RampageEntry
                                                    {
                                                        MatchId = matchId,
                                                        HeroId = heroId,
                                                        HeroName = heroName,
                                                        StartTime = m2.StartTime,
                                                        MatchDate = m2.StartTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(m2.StartTime.Value).UtcDateTime : null
                                                    };
                                                    await data.AppendRampageAsync(pid, ramp);
                                                    Logger.LogRampageFound(pid, matchId, heroName);
                                                    rampageAddsPerPlayer.AddOrUpdate(pid, 1, (_, v) => v + 1);
                                                }
                                            }
                                        }
                                    }
                                    catch (Exception ex) { Logger.Warn($"[new] get match after poll failed for {matchId}: {ex.Message}"); }

                                    foreach (var pid in matchToPlayers[matchId])
                                    {
                                        await data.SetLastCheckedAsync(pid, matchId);
                                    }
                                    return;
                                }
                            }

                            await data.EnqueueGlobalParseAsync(matchId, jobId, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                            foreach (var pid in matchToPlayers[matchId])
                            {
                                await data.SetLastCheckedAsync(pid, matchId);
                            }
                        }
                    }
                    finally
                    {
                        try { throttler.Release(); } catch { }
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);

            // 5) Update READMEs for players with new rampages and main page
            foreach (var kv in rampageAddsPerPlayer)
            {
                await ReadmeGenerator.UpdatePlayerAsync(kv.Key, kv.Value);
            }
            await ReadmeGenerator.UpdateMainAsync(players);
        }

        private static async Task<List<PlayerMatchSummary>> FetchAllNewMatchesAsync(ApiManager api, long playerId, long lastChecked, CancellationToken ct)
        {
            var all = new List<PlayerMatchSummary>(512);
            var offset = 0;
            const int pageSize = 500; // OpenDota supports limit param; default is 100
            while (!ct.IsCancellationRequested)
            {
                var page = await api.GetPlayerMatchesAsync(playerId, limit: pageSize, offset: offset) ?? Array.Empty<PlayerMatchSummary>();
                if (page.Length == 0) break;
                all.AddRange(page);
                // If the oldest match in this page is still newer than lastChecked, continue; else we can stop
                var minId = page.Min(m => m.MatchId);
                if (minId <= lastChecked) break;
                offset += page.Length;
                // Safety cap to avoid infinite loops if API ignores offset
                if (offset > 100_000) break;
            }
            // Filter and sort strictly newer than lastChecked
            return all.Where(s => s.MatchId > lastChecked).OrderBy(s => s.MatchId).ToList();
        }

        public static async Task RunParsingOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct)
        {
            Logger.Info("[parse] Draining GlobalParseQueue.json ...");
            int handled = 0, rampagesFound = 0, requeued = 0, dropped = 0;

            while (!ct.IsCancellationRequested)
            {
                var (next, _) = await data.DequeueDueGlobalAsync(DateTime.UtcNow);
                if (next == null)
                {
                    break; // nothing due right now
                }

                handled++;
                if (handled % 10 == 1)
                {
                    Logger.Info($"[parse] Processing {handled} (Match {next.MatchId}, tries={next.Tries}, job={(next.JobId?.ToString() ?? "null")})");
                }

                bool? done = null;
                if (next.JobId.HasValue)
                {
                    done = await api.CheckJobAsync(next.JobId.Value);
                }

                if (done == true)
                {
                    // Parsed: fetch match and evaluate rampages for all tracked players
                    var match = await api.GetMatchAsync(next.MatchId);
                    if (match?.Players != null)
                    {
                        // Build a summary across all players (not only tracked) for visibility
                        try
                        {
                            IEnumerable<(long? AccountId, string? HeroName, int Count)> rampagers = match.Players
                                .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                .Select(p => (AccountId: (long?)p.AccountId, HeroName: (string?)Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p!.MultiKills![5]))
                                .ToList();
                            Logger.LogMatchRampageSummary(match.MatchId, rampagers);
                        }
                        catch { /* best effort summary */ }

                        foreach (var pid in players)
                        {
                            var mp = match.Players.FirstOrDefault(x => x.AccountId == (int)pid);
                            var cnt = 0;
                            if (mp?.MultiKills != null) mp.MultiKills.TryGetValue(5, out cnt);
                            var heroId = mp?.HeroId;
                            var heroNameForLog = Core.HeroCatalog.GetLocalizedName(heroId);
                            var isRampage = cnt > 0;
                            Logger.LogMatchEvaluation(pid, match.MatchId, heroNameForLog, cnt, isRampage);
                            if (isRampage)
                            {
                                var heroName = heroNameForLog;
                                var ramp = new RampageEntry
                                {
                                    MatchId = match.MatchId,
                                    HeroId = heroId,
                                    HeroName = heroName,
                                    StartTime = match.StartTime,
                                    MatchDate = match.StartTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).UtcDateTime : null
                                };
                                await data.AppendRampageAsync(pid, ramp);
                                Logger.LogRampageFound(pid, match.MatchId, heroName);
                                rampagesFound++;
                                await ReadmeGenerator.UpdatePlayerAsync(pid, 1);
                            }
                        }
                    }
                    // do not requeue parsed matches
                }
                else
                {
                    // Not done (or null): ensure a job and requeue with backoff
                    var newJob = await api.RequestParseAsync(next.MatchId);
                    var tries = next.Tries + 1;
                    if (tries >= MaxQueueTries)
                    {
                        Logger.Warn($"[parse] Drop {next.MatchId} after {tries} tries");
                        dropped++;
                    }
                    else
                    {
                        var nextCheck = DateTime.UtcNow.Add(InitialDelay);
                        await data.EnqueueGlobalParseAsync(next.MatchId, newJob, tries, nextCheck);
                        requeued++;
                        Logger.Info($"[parse] Re-queue {next.MatchId} (job={(newJob?.ToString() ?? "null")}, next in {InitialDelay.TotalSeconds:F0}s)");
                    }
                }
            }

            Logger.Info($"[parse] Done. Handled={handled}, Rampages={rampagesFound}, Requeued={requeued}, Dropped={dropped}");
            await ReadmeGenerator.UpdateMainAsync(players);
        }

        private static async Task<bool> IsRampageAsync(ApiManager api, long matchId, long playerId)
        {
            var match = await api.GetMatchAsync(matchId);
            if (match?.Players == null) return false;
            var p = match.Players.FirstOrDefault(x => x.AccountId == (int)playerId);
            return p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var cnt) && cnt > 0;
        }

    private static async Task<bool> PollJobTwiceAsync(ApiManager api, long jobId, TimeSpan delay, CancellationToken ct)
        {
            for (int i = 0; i < InitialTries; i++)
            {
                try { await Task.Delay(delay, ct); } catch { }
                var done = await api.CheckJobAsync(jobId);
                if (done == true) return true;
            }
            return false;
        }

        private static async Task EnqueueParseAsync(DataManager data, long playerId, long matchId, long? jobId, int tries, DateTime nextCheck)
        {
            // Centralized queue: write to GlobalParseQueue.json (per-player queue kept only for migration/back-compat)
            await data.EnqueueGlobalParseAsync(matchId, jobId, tries, nextCheck);
        }

        public static async Task RegenReadmeAsync(DataManager data, List<long> players)
        {
            // Only local files are used; no API calls
                // Best-effort: enrich missing local data (profiles and matches) without parsing
                try
                {
                    using var http = new System.Net.Http.HttpClient();
                    http.DefaultRequestHeaders.UserAgent.ParseAdd("RampageTracker/1.0 (+https://opendota.com)");
                    var api = new ApiManager(http, data.GetApiKey());
                    foreach (var playerId in players)
                    {
                        try
                        {
                            // Always refresh profile and matches to keep data current; safe and fast
                            var prof = await api.GetPlayerProfileAsync(playerId);
                            if (prof != null) await data.SavePlayerProfileAsync(playerId, prof);
                            var matches = await api.GetPlayerMatchesAsync(playerId);
                            if (matches != null) await data.SavePlayerMatchesAsync(playerId, matches);
                        }
                        catch { }
                    }
                }
                catch { }

                // Generate per-player pages for all known players
                foreach (var playerId in players)
                {
                    await ReadmeGenerator.UpdatePlayerAsync(playerId, 0);
                }
                await ReadmeGenerator.UpdateMainAsync(players);
        }
    }
}
