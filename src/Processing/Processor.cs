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
    private static readonly TimeSpan InitialDelay =
#if DEBUG
        TimeSpan.FromMilliseconds(50);
#else
        TimeSpan.FromMinutes(3);
#endif
    private const int InitialTries = 1;
        private const int MaxQueueTries = 15;

        public static async Task RunNewOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct)
        {
            foreach (var playerId in players)
            {
                if (ct.IsCancellationRequested) break;

                Logger.Info($"[new] Player {playerId}");
                var last = data.GetLastChecked(playerId);
                var summaries = (await api.GetPlayerMatchesAsync(playerId)) ?? Array.Empty<PlayerMatchSummary>();
                var newMatches = summaries.Where(s => s.MatchId > last).OrderBy(s => s.MatchId).ToList();
                if (newMatches.Count == 0)
                {
                    Logger.Info($"[new] Player {playerId}: keine neuen Matches.");
                    continue;
                }

                var foundRampages = new List<long>();

                foreach (var s in newMatches)
                {
                    if (ct.IsCancellationRequested) break;

                    var hasParsed = await api.GetHasParsedAsync(s.MatchId);
                    if (hasParsed == true)
                    {
                        var isRampage = await IsRampageAsync(api, s.MatchId, playerId);
                        if (isRampage) foundRampages.Add(s.MatchId);
                    }
                    else
                    {
                        var jobId = await api.RequestParseAsync(s.MatchId);
                        if (jobId == null)
                        {
                            await EnqueueParseAsync(data, playerId, s.MatchId, null, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                            goto watermark; // lastChecked aktualisieren
                        }

                        var success = await PollJobTwiceAsync(api, jobId.Value, InitialDelay, ct);
                        if (success)
                        {
                            var isRampage = await IsRampageAsync(api, s.MatchId, playerId);
                            if (isRampage) foundRampages.Add(s.MatchId);
                        }
                        else
                        {
                            await EnqueueParseAsync(data, playerId, s.MatchId, jobId, InitialTries, DateTime.UtcNow.Add(InitialDelay));
                        }
                    }

                watermark:
                    data.SetLastChecked(playerId, s.MatchId);
                }

                if (foundRampages.Count > 0)
                {
                    await data.AppendRampagesAsync(playerId, foundRampages);
                    await ReadmeGenerator.UpdatePlayerAsync(playerId, foundRampages.Count);
                    Logger.Info($"[new] Player {playerId}: {foundRampages.Count} Rampages gefunden.");
                }
            }

            await ReadmeGenerator.UpdateMainAsync(players);
        }

        public static async Task RunParsingOnlyAsync(ApiManager api, DataManager data, List<long> players, int workers, CancellationToken ct)
        {
            foreach (var playerId in players)
            {
                var queue = await data.LoadQueueAsync(playerId);
                if (queue.Count == 0) continue;

                Logger.Info($"[parse] Player {playerId}: {queue.Count} Einträge");
                var foundRampages = new List<long>();
                var now = DateTime.UtcNow;

                foreach (var entry in queue.ToList())
                {
                    if (ct.IsCancellationRequested) break;

                    if (entry.NextCheckAtUtc.HasValue && entry.NextCheckAtUtc.Value > now)
                        continue;

                    bool? done = null;
                    if (entry.JobId.HasValue)
                    {
                        done = await api.CheckJobAsync(entry.JobId.Value);
                    }

                    if (done == true)
                    {
                        if (await IsRampageAsync(api, entry.MatchId, playerId))
                        {
                            foundRampages.Add(entry.MatchId);
                        }
                        queue.Remove(entry);
                    }
                    else if (done == false || entry.JobId == null)
                    {
                        // invalid job or none -> request new, schedule next check
                        var newJob = await api.RequestParseAsync(entry.MatchId);
                        entry.JobId = newJob;
                        entry.Tries++;
                        entry.NextCheckAtUtc = DateTime.UtcNow.Add(InitialDelay);
                        if (entry.Tries >= MaxQueueTries)
                        {
                            Logger.Warn($"[parse] Drop {entry.MatchId} nach {entry.Tries} Tries");
                            queue.Remove(entry);
                        }
                    }
                    else
                    {
                        // not finished yet -> reschedule
                        entry.Tries++;
                        entry.NextCheckAtUtc = DateTime.UtcNow.Add(InitialDelay);
                        if (entry.Tries >= MaxQueueTries)
                        {
                            Logger.Warn($"[parse] Drop {entry.MatchId} nach {entry.Tries} Tries");
                            queue.Remove(entry);
                        }
                    }
                }

                if (foundRampages.Count > 0)
                {
                    await data.AppendRampagesAsync(playerId, foundRampages);
                    await ReadmeGenerator.UpdatePlayerAsync(playerId, foundRampages.Count);
                }

                await data.SaveQueueAsync(playerId, queue);
            }

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
    }
}
