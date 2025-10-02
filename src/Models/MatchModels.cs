using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RampageTracker.Models
{
    public class PlayerMatchSummary
    {
        [JsonProperty("match_id")] public long MatchId { get; set; }
        [JsonProperty("start_time")] public long? StartTime { get; set; }
    }

    public class Match
    {
        [JsonProperty("match_id")] public long MatchId { get; set; }
        [JsonProperty("start_time")] public long? StartTime { get; set; }
        [JsonProperty("players")] public List<MatchPlayer>? Players { get; set; }
    }

    public class MatchPlayer
    {
        [JsonProperty("account_id")] public int AccountId { get; set; }
        [JsonProperty("multi_kills")] public Dictionary<int, int>? MultiKills { get; set; }
    }

    public class ParseQueueEntry
    {
        public long MatchId { get; set; }
        public long? JobId { get; set; }
        public int Tries { get; set; }
        public DateTime? NextCheckAtUtc { get; set; }
    }
}
