using System.Collections.Generic;
using Newtonsoft.Json;

namespace OpenDotaRampage.Models
{
    public class Player
    {
        [JsonProperty("match_id")]
        public long MatchId { get; set; }

        [JsonProperty("account_id")]
        public int? AccountId { get; set; }

        [JsonProperty("hero_id")]
        public int? HeroId { get; set; }

        [JsonProperty("multi_kills")]
        public Dictionary<int, int>? MultiKills { get; set; }
    }

    public class Match
    {
        [JsonProperty("match_id")]
        public long MatchId { get; set; }

        // Unix epoch seconds; nullable to handle older cached matches without this field
        [JsonProperty("start_time")]
        public long? StartTime { get; set; }

        [JsonProperty("players")]
        public List<Player>? Players { get; set; }
    }
    public class Hero
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("localized_name")]
        public string LocalizedName { get; set; } = string.Empty;

        [JsonProperty("primary_attr")]
        public string PrimaryAttr { get; set; } = string.Empty;

        [JsonProperty("attack_type")]
        public string AttackType { get; set; } = string.Empty;

        [JsonProperty("roles")]
        public string[] Roles { get; set; } = Array.Empty<string>();

        [JsonProperty("legs")]
        public int Legs { get; set; }
    }
    public class JobResponse
    {
        [JsonProperty("job")]
        public Job Job { get; set; } = new Job();
    }

    public class Job
    {
        [JsonProperty("jobId")]
        public string JobId { get; set; } = string.Empty;
    }
     public class CountsResponse
    {
        [JsonProperty("leaver_status")]
        public Dictionary<string, WinLoss> LeaverStatus { get; set; } = new();

        [JsonProperty("game_mode")]
    public Dictionary<string, WinLoss> GameMode { get; set; } = new();

        [JsonProperty("lobby_type")]
    public Dictionary<string, WinLoss> LobbyType { get; set; } = new();

        [JsonProperty("lane_role")]
    public Dictionary<string, WinLoss> LaneRole { get; set; } = new();

        [JsonProperty("region")]
    public Dictionary<string, WinLoss> Region { get; set; } = new();

        [JsonProperty("patch")]
    public Dictionary<string, WinLoss> Patch { get; set; } = new();

        [JsonProperty("is_radiant")]
        public Dictionary<string, WinLoss> IsRadiant { get; set; } = new();
    }

    public class WinLoss
    {
        [JsonProperty("games")]
        public int Games { get; set; }

        [JsonProperty("win")]
        public int Win { get; set; }
    }
}
