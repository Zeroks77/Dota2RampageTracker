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
                var summaries = (await api.GetPlayerMatchesAsync(playerId)) ?? Array.Empty<PlayerMatchSummary>();
                var newMatches = summaries.Where(s => s.MatchId > last).OrderBy(s => s.MatchId).ToList();
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
                                try { isRampage = await IsRampageAsync(api, sLocal.MatchId, playerId); }
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
                                        try { isRampage = await IsRampageAsync(api, sLocal.MatchId, playerId); }
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
                        foreach (var pid in players)
                        {
                            var mp = match.Players.FirstOrDefault(x => x.AccountId == (int)pid);
                            if (mp?.MultiKills != null && mp.MultiKills.TryGetValue(5, out var cnt) && cnt > 0)
                            {
                                var heroId = mp.HeroId;
                                var heroName = Core.HeroCatalog.GetLocalizedName(heroId);
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
            var queue = await data.LoadQueueAsync(playerId);
            var existing = queue.FirstOrDefault(q => q.MatchId == matchId);
            if (existing == null)
            {
                queue.Add(new ParseQueueEntry
                {
                    MatchId = matchId,
                    JobId = jobId,
                    Tries = tries,
                    NextCheckAtUtc = nextCheck
                });
            }
            else
            {
                existing.JobId = jobId;
                existing.Tries = Math.Max(existing.Tries, tries);
                existing.NextCheckAtUtc = nextCheck;
            }
            await data.SaveQueueAsync(playerId, queue);
        }

        public static async Task RegenReadmeAsync(DataManager data, List<long> players)
        {
            // Only local files are used; no API calls
            await Rendering.ReadmeGenerator.UpdateMainAsync(players);
            foreach (var pid in players)
            {
                await Rendering.ReadmeGenerator.UpdatePlayerAsync(pid, newFound: 0);
            }
        }
    }
}
