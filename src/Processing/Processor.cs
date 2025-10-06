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
            foreach (var playerId in players)
            {
                if (ct.IsCancellationRequested) break;

                Logger.Info($"[new] Player {playerId}");
                var last = await data.GetLastCheckedAsync(playerId);
                var newMatches = await FetchAllNewMatchesAsync(api, playerId, last, ct);
                if (newMatches.Count == 0)
                {
                    Logger.Info($"[new] Player {playerId}: keine neuen Matches.");
                    continue;
                }

                var foundRampages = new List<long>();

                int processed = 0;
                var throttler = new System.Threading.SemaphoreSlim(Math.Max(1, workers));
                var tasks = new List<Task>();

                foreach (var s in newMatches)
                {
                    await throttler.WaitAsync(ct);
                    var sLocal = s;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            if (ct.IsCancellationRequested) return;

                            var idx = Interlocked.Increment(ref processed);
                            if (idx % 10 == 1 || idx == newMatches.Count)
                            {
                                Logger.Info($"[new] Player {playerId}: verarbeite {idx}/{newMatches.Count} (Match {sLocal.MatchId})");
                            }

                            bool? hasParsed = null;
                            try
                            {
                                hasParsed = await api.GetHasParsedAsync(sLocal.MatchId);
                            }
                            catch (Exception ex)
                            {
                                Logger.Warn($"[new] has_parsed failed for {sLocal.MatchId}: {ex.Message}");
                            }
                            if (hasParsed == true)
                            {
                                bool isRampage = false;
                                try {
                                    // Fetch and log evaluation details
                                    var match = await api.GetMatchAsync(sLocal.MatchId);
                                    if (match?.Players != null)
                                    {
                                        // per-match summary (all players that rampaged)
                                        try
                                        {
                                            var rampagers = match.Players
                                                .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                                .Select(p => (AccountId: (long?)p.AccountId, HeroName: Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p.MultiKills![5]))
                                                .ToList();
                                            Logger.LogMatchRampageSummary(match.MatchId, rampagers);
                                        }
                                        catch { }

                                        var mp = match.Players.FirstOrDefault(x => x.AccountId == (int)playerId);
                                        var cnt = 0; if (mp?.MultiKills != null) mp.MultiKills.TryGetValue(5, out cnt);
                                        var heroId = mp?.HeroId;
                                        var heroName = Core.HeroCatalog.GetLocalizedName(heroId);
                                        isRampage = cnt > 0;
                                        Logger.LogMatchEvaluation(playerId, sLocal.MatchId, heroName, cnt, isRampage);
                                    }
                                }
                                catch (Exception ex) { Logger.Warn($"[new] get match failed for {sLocal.MatchId}: {ex.Message}"); }
                                if (isRampage)
                                {
                                    lock (foundRampages) foundRampages.Add(sLocal.MatchId);
                                    await _rampageWrite.WaitAsync(ct);
                                    try { await data.AppendRampagesAsync(playerId, new List<long> { sLocal.MatchId }); }
                                    finally { _rampageWrite.Release(); }
                                }
                            }
                            else
                            {
                                long? jobId = null;
                                try { jobId = await api.RequestParseAsync(sLocal.MatchId); }
                                catch (Exception ex) { Logger.Warn($"[new] request parse failed for {sLocal.MatchId}: {ex.Message}"); }
                                if (eagerPoll && jobId != null)
                                {
                                    var quickDelay = TimeSpan.FromMilliseconds(25);
                                    bool success = false;
                                    try { success = await PollJobTwiceAsync(api, jobId.Value, quickDelay, ct); }
                                    catch (Exception ex) { Logger.Warn($"[new] poll failed for job {jobId}: {ex.Message}"); }
                                    if (success)
                                    {
                                        bool isRampage = false;
                                        try
                                        {
                                            var match = await api.GetMatchAsync(sLocal.MatchId);
                                            if (match?.Players != null)
                                            {
                                                // per-match summary
                                                try
                                                {
                                                    var rampagers = match.Players
                                                        .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                                        .Select(p => (AccountId: (long?)p.AccountId, HeroName: Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p.MultiKills![5]))
                                                        .ToList();
                                                    Logger.LogMatchRampageSummary(match.MatchId, rampagers);
                                                }
                                                catch { }

                                                var mp = match.Players.FirstOrDefault(x => x.AccountId == (int)playerId);
                                                var cnt = 0; if (mp?.MultiKills != null) mp.MultiKills.TryGetValue(5, out cnt);
                                                var heroId = mp?.HeroId; var heroName = Core.HeroCatalog.GetLocalizedName(heroId);
                                                isRampage = cnt > 0;
                                                Logger.LogMatchEvaluation(playerId, sLocal.MatchId, heroName, cnt, isRampage);
                                            }
                                        }
                                        catch (Exception ex) { Logger.Warn($"[new] get match after poll failed for {sLocal.MatchId}: {ex.Message}"); }
                                        if (isRampage)
                                        {
                                            lock (foundRampages) foundRampages.Add(sLocal.MatchId);
                                            await _rampageWrite.WaitAsync(ct);
                                            try { await data.AppendRampagesAsync(playerId, new List<long> { sLocal.MatchId }); }
                                            finally { _rampageWrite.Release(); }
                                        }
                                        await data.SetLastCheckedAsync(playerId, sLocal.MatchId);
                                        return;
                                    }
                                }
                                await EnqueueParseAsync(data, playerId, sLocal.MatchId, jobId, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                            }

                            await data.SetLastCheckedAsync(playerId, sLocal.MatchId);
                        }
                        finally
                        {
                            try { throttler.Release(); } catch { }
                        }
                    }, ct));
                }

                await Task.WhenAll(tasks);

                if (foundRampages.Count > 0)
                {
                    await data.AppendRampagesAsync(playerId, foundRampages); // idempotent: bereits inkrementell geschrieben
                    await ReadmeGenerator.UpdatePlayerAsync(playerId, foundRampages.Count);
                    Logger.Info($"[new] Player {playerId}: {foundRampages.Count} Rampages gefunden.");
                }
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
                            var rampagers = match.Players
                                .Where(p => p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var v) && v > 0)
                                .Select(p => (AccountId: (long?)p.AccountId, HeroName: Core.HeroCatalog.GetLocalizedName(p.HeroId), Count: p.MultiKills![5]))
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
