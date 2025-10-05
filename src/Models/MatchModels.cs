using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace RampageTracker.Models
{
    public class PlayerMatchSummary
    {
        [JsonProperty("match_id")] public long MatchId { get; set; }
        [JsonProperty("start_time")] public long? StartTime { get; set; }
        [JsonProperty("hero_id")] public int? HeroId { get; set; }
        [JsonProperty("player_slot")] public int? PlayerSlot { get; set; }
        [JsonProperty("radiant_win")] public bool? RadiantWin { get; set; }
    }

    public class Match
    {
        [JsonProperty("match_id")] public long MatchId { get; set; }
        [JsonProperty("start_time")] public long? StartTime { get; set; }
        [JsonProperty("players")] public List<MatchPlayer>? Players { get; set; }
    }

    public class MatchPlayer
    {
        [JsonProperty("account_id")] public int? AccountId { get; set; }
        [JsonProperty("hero_id")] public int? HeroId { get; set; }
        [JsonProperty("multi_kills")] public Dictionary<int, int>? MultiKills { get; set; }
    }

    // Minimal player profile structures for README generation
    public class PlayerProfileResponse
    {
        [JsonProperty("profile")] public PlayerProfile? Profile { get; set; }
    }

    public class PlayerProfile
    {
        [JsonProperty("account_id")] public long? AccountId { get; set; }
        [JsonProperty("personaname")] public string? PersonaName { get; set; }
        [JsonProperty("avatar")] public string? Avatar { get; set; }
        [JsonProperty("avatarfull")] public string? AvatarFull { get; set; }
        [JsonProperty("profileurl")] public string? ProfileUrl { get; set; }
    }

    public class ParseQueueEntry
    {
        public long MatchId { get; set; }
        public long? JobId { get; set; }
        public int Tries { get; set; }
        public DateTime? NextCheckAtUtc { get; set; }
    }

    public class RampageEntry
    {
        public long MatchId { get; set; }
        public string HeroName { get; set; } = "Unknown";
        public int? HeroId { get; set; }
        public DateTime? MatchDate { get; set; }
        public long? StartTime { get; set; } // Unix timestamp from API
    }

    // Minimal representation of OpenDota /heroes
    public class HeroRef
    {
        [JsonProperty("id")] public int Id { get; set; }
        [JsonProperty("localized_name")] public string? LocalizedName { get; set; }
        [JsonProperty("name")] public string? InternalName { get; set; }
    }
}
