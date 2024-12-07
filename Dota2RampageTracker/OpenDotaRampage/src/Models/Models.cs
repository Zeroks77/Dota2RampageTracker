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
        public Dictionary<int, int> MultiKills { get; set; }
    }

    public class Match
    {
        [JsonProperty("match_id")]
        public long MatchId { get; set; }

        [JsonProperty("players")]
        public List<Player> Players { get; set; }
    }
  public class Hero
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("localized_name")]
        public string LocalizedName { get; set; }

        [JsonProperty("primary_attr")]
        public string PrimaryAttr { get; set; }

        [JsonProperty("attack_type")]
        public string AttackType { get; set; }

        [JsonProperty("roles")]
        public string[] Roles { get; set; }

        [JsonProperty("legs")]
        public int Legs { get; set; }
    }
    public class JobResponse
    {
        [JsonProperty("job")]
        public Job Job { get; set; }
    }

    public class Job
    {
        [JsonProperty("jobId")]
        public string JobId { get; set; }
    }
}
