using System;
using System.Collections.Concurrent;
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
    // Per-run cache: per (matchId, playerId) we remember positive/negative results
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(long matchId, long playerId), bool> _rampageCheckCache = new();
        // Per-run dedupe for parse requests per match
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task<long?>> _parseRequestTasks = new();
        // Per-run dedupe for hasParsed checks per match
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, Task<bool?>> _hasParsedTasks = new();
        // Per-run guard to avoid enqueueing the same match multiple times into the global queue
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _globalEnqueueSet = new();
    // Per-run guard: process already-parsed matches only once across all players
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> _parsedMatchProcessed = new();
        private static readonly System.Threading.SemaphoreSlim _rampageWrite = new(1, 1);
    private static readonly TimeSpan InitialDelay =
#if DEBUG
        TimeSpan.FromMilliseconds(50);
#else
        (Environment.GetEnvironmentVariable("RT_FAST_DELAY") == "1")
        ? TimeSpan.FromMilliseconds(25)
        : TimeSpan.FromMinutes(3);
#endif
        private const int InitialTries = 1;
        private const int MaxQueueTries = 15;

        public static async Task RunNewOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct)
        {
            var tracked = new HashSet<long>(players);
            foreach (var playerId in players)
            {
                if (ct.IsCancellationRequested) break;

                Logger.Info($"🔍 Processing Player {playerId}...");
                var last = await data.GetLastCheckedAsync(playerId);
                Logger.Debug($"Player {playerId}: last checked match {last}");
                var summaries = (await api.GetPlayerMatchesAsync(playerId)) ?? Array.Empty<PlayerMatchSummary>();
                // Persist summaries for README aggregation
                try { await data.SavePlayerMatchesAsync(playerId, summaries); } catch { }
                // Persist profile for avatar/name in README
                try { var prof = await api.GetPlayerProfileAsync(playerId); if (prof != null) await data.SavePlayerProfileAsync(playerId, prof); } catch { }
                var newMatches = summaries.Where(s => s.MatchId > last)
                                         .GroupBy(s => s.MatchId)
                                         .Select(g => g.First())
                                         .OrderBy(s => s.MatchId)
                                         .ToList();
                if (newMatches.Count == 0)
                {
                    Logger.Info($"👤 Player {playerId}: no new matches since {last}");
                    continue;
                }
                Logger.Info($"👤 Player {playerId}: {newMatches.Count} new matches to process");

                var foundRampages = new ConcurrentBag<long>();
                var throttler = new SemaphoreSlim(Math.Max(1, workers));
                
                // PARALLEL processing - all matches processed simultaneously!
                var processingTasks = newMatches.Select(async s =>
                {
                    await throttler.WaitAsync(ct);
                    try
                    {
                        // Skip if we already know this match has no rampage for THIS player
                        if (_rampageCheckCache.TryGetValue((s.MatchId, playerId), out var neg) && neg == false)
                        {
                            return;
                        }
                        var hasParsed = await _hasParsedTasks.GetOrAdd(s.MatchId, _ => api.GetHasParsedAsync(s.MatchId));
                        if (hasParsed == true)
                        {
                            // Process parsed match once across all players
                            if (_parsedMatchProcessed.TryAdd(s.MatchId, 0))
                            {
                                var match = await api.GetMatchAsync(s.MatchId);
                                if (match?.Players != null)
                                {
                                    foreach (var mp in match.Players)
                                    {
                                        if (!mp.AccountId.HasValue) continue;
                                        var pid = (long)mp.AccountId.Value;
                                        if (!tracked.Contains(pid)) continue;
                                        var isRampage = mp.MultiKills != null && mp.MultiKills.TryGetValue(5, out var cnt) && cnt > 0;
                                        _rampageCheckCache[(s.MatchId, pid)] = isRampage;
                                        if (isRampage)
                                        {
                                            var heroName = HeroCatalog.GetLocalizedName(mp.HeroId);
                                            var matchDate = match.StartTime.HasValue
                                                ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).DateTime
                                                : (DateTime?)null;
                                            var rampage = new RampageEntry
                                            {
                                                MatchId = s.MatchId,
                                                HeroName = heroName,
                                                HeroId = mp.HeroId,
                                                MatchDate = matchDate,
                                                StartTime = match.StartTime
                                            };
                                            await data.AppendRampageAsync(pid, rampage);
                                            Logger.LogRampageFound(pid, s.MatchId, heroName);
                                            if (pid == playerId) foundRampages.Add(s.MatchId);
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // Already processed globally; rely on cache to know if this player had a rampage
                                if (_rampageCheckCache.TryGetValue((s.MatchId, playerId), out var pos) && pos == true)
                                {
                                    foundRampages.Add(s.MatchId);
                                }
                            }
                        }
                        else
                        {
                            // Deduplicate parse requests across players
                            var jobId = await _parseRequestTasks.GetOrAdd(s.MatchId, _ => api.RequestParseAsync(s.MatchId));
                            if (jobId == null)
                            {
                                // Ensure we enqueue globally only once per match in this run
                                if (_globalEnqueueSet.TryAdd(s.MatchId, 0))
                                {
                                    await EnqueueParseAsync(data, playerId, s.MatchId, null, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                                }
                            }
                            else
                            {
                                var success = await PollJobTwiceAsync(api, jobId.Value, InitialDelay, ct);
                                if (success)
                                {
                                    var rampage = await GetRampageAsync(api, s.MatchId, playerId);
                                    if (rampage != null) 
                                    {
                                        foundRampages.Add(s.MatchId);
                                        await data.AppendRampageAsync(playerId, rampage); // Sofort speichern!
                                    }
                                }
                                else
                                {
                                    if (_globalEnqueueSet.TryAdd(s.MatchId, 0))
                                    {
                                        await EnqueueParseAsync(data, playerId, s.MatchId, jobId, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"Processing match {s.MatchId}", ex, playerId, s.MatchId);
                    }
                    finally
                    {
                        // Watermark immer fortschreiben, damit ein Ausfall nicht blockiert
                        await data.SetLastCheckedAsync(playerId, s.MatchId);
                        throttler.Release();
                    }
                });

                // Wait for ALL parallel tasks to complete
                await Task.WhenAll(processingTasks);

                // Rampages wurden bereits sofort gespeichert, nur noch Statistiken und Updates
                var rampageCount = foundRampages.Count;
                Logger.LogPlayerProgress(playerId, newMatches.Count, newMatches.Count, rampageCount);
                if (rampageCount > 0)
                {
                    await ReadmeGenerator.UpdatePlayerAsync(playerId, rampageCount);
                    Logger.Success($"🎯 Player {playerId}: {rampageCount} rampages found and saved!");
                }
            }

            await ReadmeGenerator.UpdateMainAsync(players);
        }

        public static async Task RunParsingOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct)
        {
            Logger.Info("[parse] Using centralized GlobalParseQueue");
            // Quick lookup of tracked players
            var tracked = new HashSet<long>(players);
            var foundPerPlayer = new ConcurrentDictionary<long, int>();

            while (!ct.IsCancellationRequested)
            {
                var (next, _) = await data.DequeueDueGlobalAsync(DateTime.UtcNow);
                if (next == null)
                {
                    // Nothing due right now -> done
                    break;
                }

                try
                {
                    bool? done = null;
                    if (next.JobId.HasValue)
                    {
                        done = await api.CheckJobAsync(next.JobId.Value);
                    }

                    if (done == true)
                    {
                        // Parse finished -> fetch match once and evaluate for ALL tracked players present
                        var match = await api.GetMatchAsync(next.MatchId);
                        if (match?.Players != null)
                        {
                            foreach (var mp in match.Players)
                            {
                                if (!mp.AccountId.HasValue) continue;
                                var pid = (long)mp.AccountId.Value;
                                if (!tracked.Contains(pid)) continue;
                                // refresh profile/matches for README on the side (best-effort)
                                try { var prof = await api.GetPlayerProfileAsync(pid); if (prof != null) await data.SavePlayerProfileAsync(pid, prof); } catch { }
                                try { var ms = await api.GetPlayerMatchesAsync(pid); if (ms != null) await data.SavePlayerMatchesAsync(pid, ms); } catch { }
                                if (mp.MultiKills != null && mp.MultiKills.TryGetValue(5, out var cnt) && cnt > 0)
                                {
                                    var heroName = HeroCatalog.GetLocalizedName(mp.HeroId);
                                    var matchDate = match.StartTime.HasValue
                                        ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).DateTime
                                        : (DateTime?)null;
                                    var rampage = new RampageEntry
                                    {
                                        MatchId = next.MatchId,
                                        HeroName = heroName,
                                        HeroId = mp.HeroId,
                                        MatchDate = matchDate,
                                        StartTime = match.StartTime
                                    };
                                    await data.AppendRampageAsync(pid, rampage);
                                    Logger.LogRampageFound(pid, next.MatchId, heroName);
                                    foundPerPlayer.AddOrUpdate(pid, 1, (_, v) => v + 1);
                                }
                            }
                        }
                        // Do not re-enqueue on success
                    }
                    else if (done == false || !next.JobId.HasValue)
                    {
                        // invalid job or none -> request new, schedule next check
                        var newJob = await _parseRequestTasks.GetOrAdd(next.MatchId, _ => api.RequestParseAsync(next.MatchId));
                        next.JobId = newJob;
                        next.Tries++;
                        if (next.Tries < MaxQueueTries)
                        {
                            next.NextCheckAtUtc = DateTime.UtcNow.Add(InitialDelay);
                            await data.EnqueueGlobalParseAsync(next.MatchId, next.JobId, next.Tries, next.NextCheckAtUtc.Value);
                        }
                        else
                        {
                            Logger.Warn($"[parse] Drop {next.MatchId} nach {next.Tries} Tries");
                        }
                    }
                    else
                    {
                        // not finished yet -> reschedule
                        next.Tries++;
                        if (next.Tries < MaxQueueTries)
                        {
                            next.NextCheckAtUtc = DateTime.UtcNow.Add(InitialDelay);
                            await data.EnqueueGlobalParseAsync(next.MatchId, next.JobId, next.Tries, next.NextCheckAtUtc.Value);
                        }
                        else
                        {
                            Logger.Warn($"[parse] Drop {next.MatchId} nach {next.Tries} Tries");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[parse] Global entry {next.MatchId} Fehler: {ex.Message}");
                }
            }

            // Update readmes for players with new rampages
            foreach (var kvp in foundPerPlayer)
            {
                await ReadmeGenerator.UpdatePlayerAsync(kvp.Key, kvp.Value);
            }
            await ReadmeGenerator.UpdateMainAsync(players);
        }

        private static async Task<RampageEntry?> GetRampageAsync(ApiManager api, long matchId, long playerId)
        {
            // First check cache if we already evaluated this match for THIS player
            if (_rampageCheckCache.TryGetValue((matchId, playerId), out var isRampage))
            {
                if (!isRampage) return null;
                // If positive, we still need hero data; fall through to fetch if not already available.
            }

            var match = await api.GetMatchAsync(matchId);
            if (match?.Players == null) 
            {
                Logger.Debug($"Match {matchId}: no player data available");
                return null;
            }
            
            var p = match.Players.FirstOrDefault(x => x.AccountId == (int)playerId);
            if (p?.MultiKills != null && p.MultiKills.TryGetValue(5, out var cnt) && cnt > 0)
            {
                var heroName = HeroCatalog.GetLocalizedName(p.HeroId);
                var matchDate = match.StartTime.HasValue 
                    ? DateTimeOffset.FromUnixTimeSeconds(match.StartTime.Value).DateTime
                    : (DateTime?)null;
                
                var rampage = new RampageEntry
                {
                    MatchId = matchId,
                    HeroName = heroName,
                    HeroId = p.HeroId,
                    MatchDate = matchDate,
                    StartTime = match.StartTime
                };
                // Cache positive result for this (match, player)
                _rampageCheckCache[(matchId, playerId)] = true;
                Logger.LogRampageFound(playerId, matchId, heroName);
                return rampage;
            }
            
            // Cache negative result for this (match, player)
            _rampageCheckCache[(matchId, playerId)] = false;
            Logger.Debug($"Match {matchId}: no rampage for player {playerId}");
            return null;
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
            // If we've already determined no rampage exists for THIS player in this run, skip enqueue
            if (_rampageCheckCache.TryGetValue((matchId, playerId), out var neg) && neg == false)
            {
                return;
            }
            await data.EnqueueGlobalParseAsync(matchId, jobId, tries, nextCheck);
        }

        // Regenerates README files using only local data; no parsing or API calls.
        public static async Task RegenReadmeAsync(DataManager data, List<long> players)
        {
            // Generate per-player pages for all known players
            foreach (var playerId in players)
            {
                // Use 0 for newFound to avoid attention line; the generator is idempotent
                await ReadmeGenerator.UpdatePlayerAsync(playerId, 0);
            }
            // Generate main README
            await ReadmeGenerator.UpdateMainAsync(players);
        }
    }
}
