using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OpenDotaRampage.Helpers
{
    public class PlayerProgress
    {
        public long PlayerId { get; set; }
        public int DiscoveredNewMatches { get; set; }
        public int RecoveredFromPending { get; set; }
        public int Enqueued { get; set; }
        public int ResolvedViaDetails { get; set; }
        public int ResolvedViaStatus { get; set; }
        public int Dropped { get; set; }
        public int Processed { get; set; }
        public int Total { get; set; }
        public int PendingRemaining { get; set; }
    }

    public static class ProgressTracker
    {
        private static readonly ConcurrentDictionary<long, PlayerProgress> _map = new();

        private static PlayerProgress Get(long pid)
        {
            return _map.GetOrAdd(pid, id => new PlayerProgress { PlayerId = id });
        }

        public static void InitPlayer(long pid)
        {
            _map.TryAdd(pid, new PlayerProgress { PlayerId = pid });
        }
        public static void SetTotalNewMatches(long pid, int total)
        {
            var p = Get(pid); p.Total = total; p.DiscoveredNewMatches = total;
        }
        public static void AddRecoveredFromPending(long pid, int n)
        {
            var p = Get(pid); p.RecoveredFromPending += n;
        }
        public static void IncEnqueued(long pid)
        {
            var p = Get(pid); p.Enqueued++;
        }
        public static void IncResolvedDetails(long pid)
        {
            var p = Get(pid); p.ResolvedViaDetails++;
        }
        public static void IncResolvedStatus(long pid)
        {
            var p = Get(pid); p.ResolvedViaStatus++;
        }
        public static void IncDropped(long pid)
        {
            var p = Get(pid); p.Dropped++;
        }
        public static void IncProcessed(long pid)
        {
            var p = Get(pid); p.Processed++;
        }
        public static void SetPendingRemaining(long pid, int n)
        {
            var p = Get(pid); p.PendingRemaining = n;
        }

        public static List<PlayerProgress> Snapshot()
        {
            return _map.Values.OrderBy(p => p.PlayerId).ToList();
        }
    }
}
