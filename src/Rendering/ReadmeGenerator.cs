using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RampageTracker.Models;
using RampageTracker.Core;

namespace RampageTracker.Rendering
{
    public static class ReadmeGenerator
    {
        // Choose the best per-player data directory between repoRoot/data and repoRoot/src/data
        private static string ChoosePlayerDataDir(string repoRoot, long playerId)
        {
            var c1 = Path.Combine(Path.Combine(repoRoot, "data"), playerId.ToString());
            var c2 = Path.Combine(Path.Combine(repoRoot, "src", "data"), playerId.ToString());

            var e1 = Directory.Exists(c1);
            var e2 = Directory.Exists(c2);
            if (e1 && !e2) return c1;
            if (!e1 && e2) return c2;
            if (!e1 && !e2)
            {
                // Neither exists, default to repoRoot/data/<id>
                return Path.Combine(Path.Combine(repoRoot, "data"), playerId.ToString());
            }

            // Both exist: pick the one with the freshest known file among Matches.json, Rampages.json, profile.json
            DateTime GetBestTime(string dir)
            {
                var times = new List<DateTime>();
                var m = Path.Combine(dir, "Matches.json");
                var r = Path.Combine(dir, "Rampages.json");
                var p = Path.Combine(dir, "profile.json");
                if (File.Exists(m)) times.Add(File.GetLastWriteTimeUtc(m));
                if (File.Exists(r)) times.Add(File.GetLastWriteTimeUtc(r));
                if (File.Exists(p)) times.Add(File.GetLastWriteTimeUtc(p));
                return times.Count > 0 ? times.Max() : DateTime.MinValue;
            }

            // Prefer the directory that actually has Matches.json; if only one has it, choose that.
            var c1HasMatches = File.Exists(Path.Combine(c1, "Matches.json"));
            var c2HasMatches = File.Exists(Path.Combine(c2, "Matches.json"));
            if (c1HasMatches && !c2HasMatches) return c1;
            if (!c1HasMatches && c2HasMatches) return c2;

            // Otherwise pick the one with the freshest timestamp among known files.
            var t1 = GetBestTime(c1);
            var t2 = GetBestTime(c2);
            if (t1 == DateTime.MinValue && t2 == DateTime.MinValue)
            {
                // As a final tie-breaker, prefer src/data because current app writes there
                return c2;
            }
            return t2 > t1 ? c2 : c1;
        }

        public static async Task UpdateMainAsync(IEnumerable<long> playerIds)
        {
            var root = Directory.GetCurrentDirectory();
            // If executed from ...\src, write README.md to parent folder
            var dirName = new DirectoryInfo(root).Name;
            if (dirName.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(root);
                if (parent != null) root = parent.FullName;
            }

            // Resolve data directory (repoRoot/data or repoRoot/src/data)
            var dataDir = Path.Combine(root, "data");
            var altDataDir = Path.Combine(root, "src", "data");
            if (!Directory.Exists(dataDir) && Directory.Exists(altDataDir))
            {
                dataDir = altDataDir;
            }

            var path = Path.Combine(root, "README.md");
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
            var lines = new List<string>();
            lines.Add("# Dota 2 Rampage Tracker");
            lines.Add($"Last updated: {now}");
            lines.Add("");
            lines.Add("> Note: All game data is sourced via the OpenDota API. This project is not affiliated with Valve or OpenDota.");
            lines.Add("> Data source: OpenDota (https://www.opendota.com)");
            lines.Add("");
            lines.Add("## Players");
            lines.Add("");
            lines.Add("| Player | Profile | Rampages/Matches | Winrate | Radiant | Dire | Rampages |");
            lines.Add("|:-------|:-------:|------------------:|--------:|--------:|-----:|:---------|");

            var rows = new List<(int rampages, string line)>();
            foreach (var pid in playerIds)
            {
                // Gather stats; prefer the freshest per-player folder between root/data and src/data
                var pdir = ChoosePlayerDataDir(root, pid);
                var rpath = Path.Combine(pdir, "Rampages.json");
                var rampageCount = 0;
                if (File.Exists(rpath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(rpath);
                        var rampages = JsonConvert.DeserializeObject<List<RampageEntry>>(json) ?? new List<RampageEntry>();
                        rampageCount = rampages.Count;
                    }
                    catch { }
                }

                // Read match summaries for W/L and recent games
                var matchesPath = Path.Combine(pdir, "Matches.json");
                int wins = 0, losses = 0; double winrate = 0; int totalMatches = 0;
                int radWins = 0, radGames = 0, direWins = 0, direGames = 0;
                try
                {
                    if (File.Exists(matchesPath))
                    {
                        var mjson = await File.ReadAllTextAsync(matchesPath);
                        var ms = JsonConvert.DeserializeObject<List<PlayerMatchSummary>>(mjson) ?? new List<PlayerMatchSummary>();
                        totalMatches = ms.Count;
                        foreach (var m in ms)
                        {
                            if (m.PlayerSlot.HasValue && m.RadiantWin.HasValue)
                            {
                                var isRadiant = (m.PlayerSlot.Value & 0x80) == 0;
                                var win = (isRadiant && m.RadiantWin.Value) || (!isRadiant && !m.RadiantWin.Value);
                                if (win) wins++; else losses++;
                                if (isRadiant)
                                {
                                    radGames++; if (win) radWins++;
                                }
                                else
                                {
                                    direGames++; if (win) direWins++;
                                }
                            }
                        }
                        var total = wins + losses;
                        if (total > 0) winrate = 100.0 * wins / total;
                    }
                }
                catch { }

                // Try to load player profile (name, avatar)
                var profileName = pid.ToString();
                var avatarUrl = $"https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png"; // fallback
                // We store a tiny profile markdown per player if exists
                var profilePath = Path.Combine(pdir, "profile.json");
                if (File.Exists(profilePath))
                {
                    try
                    {
                        var pjson = await File.ReadAllTextAsync(profilePath);
                        var prof = JsonConvert.DeserializeObject<PlayerProfile>(pjson);
                        if (!string.IsNullOrWhiteSpace(prof?.PersonaName)) profileName = prof!.PersonaName!;
                        if (!string.IsNullOrWhiteSpace(prof?.AvatarFull)) avatarUrl = prof!.AvatarFull!;
                    }
                    catch { }
                }

                var playerLink = $"[{profileName}](Players/{pid}/README.md)";
                var profilePic = string.IsNullOrWhiteSpace(avatarUrl) ? "" : $"<img src=\"{avatarUrl}\" width=\"32\" height=\"32\"/>";
                var rampagesLink = $"[View](Players/{pid}/Rampages.md)";
                var rr = radGames > 0 ? (100.0 * radWins / radGames).ToString("F2") + "%" : "-";
                var dr = direGames > 0 ? (100.0 * direWins / direGames).ToString("F2") + "%" : "-";
                var wr = (wins + losses) > 0 ? (100.0 * wins / (wins + losses)).ToString("F2") + "%" : "-";
                rows.Add((rampageCount, $"| {playerLink} | {profilePic} | {rampageCount}/{totalMatches} | {wr} | {rr} | {dr} | {rampagesLink} |"));
            }

            // Sort by rampage count desc, then by player link
            foreach (var row in rows.OrderByDescending(r => r.rampages).ThenBy(r => r.line))
            {
                lines.Add(row.line);
            }

            // How it works section (en)
            lines.Add("");
            lines.Add("## How it works");
            lines.Add("");
            lines.Add("- For each player, the tool fetches recent match summaries from OpenDota (no replays).\n- For new/unparsed matches, it requests a parse job and later evaluates the full match payload.\n- A centralized parse queue avoids duplicate requests for matches shared by multiple tracked players.\n- Rampages are detected when multi_kills[5] > 0 for the player in match data.\n- Results are stored per player under `data/<playerId>/` (Rampages.json, Matches.json, profile.json, lastchecked.txt).\n- The README files are generated from these local files (no parsing required). ");
            lines.Add("");
            lines.Add("### Modes");
            lines.Add("- `new`: checks new matches per player, requests parsing if necessary, writes rampages, updates READMEs.\n- `parse`: drains the global parse queue, writes found rampages, updates READMEs.\n- `full`: clears local data and runs like `new`.\n- `regen-readme`: generates only the READMEs from local files (no API/parsing).");

            await File.WriteAllLinesAsync(path, lines);
        }

        public static async Task UpdatePlayerAsync(long playerId, int newFound)
        {
            var root = Directory.GetCurrentDirectory();
            var dirName = new DirectoryInfo(root).Name;
            if (dirName.Equals("src", StringComparison.OrdinalIgnoreCase))
            {
                var parent = Directory.GetParent(root);
                if (parent != null) root = parent.FullName;
            }

            // Resolve data directory roots
            var dataDir = Path.Combine(root, "data");
            var altDataDir = Path.Combine(root, "src", "data");
            if (!Directory.Exists(dataDir) && Directory.Exists(altDataDir))
            {
                dataDir = altDataDir;
            }

            var playerDir = Path.Combine(root, "Players", playerId.ToString());
            Directory.CreateDirectory(playerDir);
            var playerReadme = Path.Combine(playerDir, "README.md");

            // Choose freshest/most complete per-player folder
            var pdata = ChoosePlayerDataDir(root, playerId);

            // Load profile info if present
            var profilePath = Path.Combine(pdata, "profile.json");
            string playerName = playerId.ToString();
            string avatar = "https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png";
            if (File.Exists(profilePath))
            {
                try
                {
                    var pjson = await File.ReadAllTextAsync(profilePath);
                    var prof = JsonConvert.DeserializeObject<PlayerProfile>(pjson);
                    if (!string.IsNullOrWhiteSpace(prof?.PersonaName)) playerName = prof!.PersonaName!;
                    if (!string.IsNullOrWhiteSpace(prof?.AvatarFull)) avatar = prof!.AvatarFull!;
                }
                catch { }
            }

            // Load matches to aggregate per-hero stats
            var matchesPath = Path.Combine(pdata, "Matches.json");
            var matches = new List<PlayerMatchSummary>();
            if (File.Exists(matchesPath))
            {
                try
                {
                    var mjson = await File.ReadAllTextAsync(matchesPath);
                    matches = JsonConvert.DeserializeObject<List<PlayerMatchSummary>>(mjson) ?? new List<PlayerMatchSummary>();
                }
                catch { }
            }

            var byHero = matches
                .Where(m => m.HeroId.HasValue)
                .GroupBy(m => m.HeroId!.Value)
                .Select(g => new
                {
                    HeroId = g.Key,
                    Games = g.Count(),
                    Wins = g.Count(m => m.PlayerSlot.HasValue && m.RadiantWin.HasValue && (((m.PlayerSlot.Value & 0x80) == 0) == m.RadiantWin.Value)),
                    Losses = g.Count(m => m.PlayerSlot.HasValue && m.RadiantWin.HasValue && (((m.PlayerSlot.Value & 0x80) == 0) != m.RadiantWin.Value)),
                    Items = g.OrderByDescending(m => m.StartTime ?? 0).Take(50).ToList()
                })
                .OrderByDescending(x => x.Games)
                .ToList();

            var lines = new List<string>();
            lines.Add($"# {playerName} <img src=\"{avatar}\" width=\"48\" height=\"48\"/>");
            lines.Add("");
            lines.Add("## Heroes (grouped by winrate)");
            lines.Add("");
            lines.Add("| Hero | Games | W | L | Winrate |");
            lines.Add("|:-----|-----:|--:|--:|--------:|");

            foreach (var h in byHero)
            {
                var name = HeroCatalog.GetLocalizedName(h.HeroId);
                var slug = HeroCatalog.GetReactSlug(h.HeroId);
                var icon = $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{slug}.png";
                var total = h.Wins + h.Losses;
                var wr = total > 0 ? 100.0 * h.Wins / total : 0.0;
                lines.Add($"| <img src=\"{icon}\" width=\"32\"/> {name} | {h.Games} | {h.Wins} | {h.Losses} | {wr:F1}% |");
            }

            lines.Add("");
            lines.Add("## Recent games (links)");
            lines.Add("");
            lines.Add("| Match | Date | Hero | Result |");
            lines.Add("|:------|:-----|:-----|:-------|");
            foreach (var m in matches.OrderByDescending(m => m.StartTime ?? 0).Take(50))
            {
                var date = m.StartTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(m.StartTime.Value).UtcDateTime.ToString("yyyy-MM-dd") : "";
                var hero = HeroCatalog.GetLocalizedName(m.HeroId);
                var isRadiant = m.PlayerSlot.HasValue ? ((m.PlayerSlot.Value & 0x80) == 0) : (bool?)null;
                var result = (isRadiant.HasValue && m.RadiantWin.HasValue) ? (((isRadiant.Value && m.RadiantWin.Value) || (!isRadiant.Value && !m.RadiantWin.Value)) ? "W" : "L") : "";
                lines.Add($"| [#{m.MatchId}](https://www.opendota.com/matches/{m.MatchId}) | {date} | {hero} | {result} |");
            }

            await File.WriteAllLinesAsync(playerReadme, lines);

            // Also render a Rampages.md from Rampages.json to keep visual list in sync with data
            try
            {
                var rampagesPath = Path.Combine(pdata, "Rampages.json");
                if (File.Exists(rampagesPath))
                {
                    var json = await File.ReadAllTextAsync(rampagesPath);
                    List<RampageEntry> rs;
                    try
                    {
                        rs = JsonConvert.DeserializeObject<List<RampageEntry>>(json) ?? new List<RampageEntry>();
                    }
                    catch
                    {
                        var ids = JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
                        rs = ids.Select(id => new RampageEntry { MatchId = id }).ToList();
                    }
                    var total = rs.Count;
                    var rmd = new List<string>
                    {
                        $"# Rampages for {playerName}",
                        string.IsNullOrWhiteSpace(avatar) ? "" : $"<img src=\"{avatar}\" width=\"48\" height=\"48\"/>",
                        "",
                        $"**Total Rampages:** {total}",
                        "",
                        "[Back to Player](./README.md)",
                        ""
                    };

                    // Group by hero name for sections, then sort each hero's rampages by date desc
                    var groups = rs
                        .Select(x => new { Entry = x, Name = string.IsNullOrWhiteSpace(x.HeroName) || x.HeroName == "Unknown" ? HeroCatalog.GetLocalizedName(x.HeroId) : x.HeroName })
                        .GroupBy(x => x.Name)
                        .OrderBy(g => g.Key);

                    foreach (var g in groups)
                    {
                        var heroName = g.Key;
                        var slug = HeroCatalog.GetReactSlug(g.First().Entry.HeroId);
                        var heroIcon = $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{slug}.png";
                        rmd.Add($"## <img src=\"{heroIcon}\" width=\"28\" style=\"vertical-align:middle\"/> {heroName}");
                        rmd.Add("");
                        foreach (var item in g.OrderByDescending(x => x.Entry.StartTime ?? 0))
                        {
                            var r = item.Entry;
                            var link = $"https://www.opendota.com/matches/{r.MatchId}";
                            var date = r.MatchDate?.ToString("yyyy-MM-dd") ?? (r.StartTime.HasValue ? DateTimeOffset.FromUnixTimeSeconds(r.StartTime.Value).UtcDateTime.ToString("yyyy-MM-dd") : "");
                            rmd.Add($"- [{r.MatchId}]({link}) — {date}");
                        }
                        rmd.Add("");
                    }
                    var rampagesMdPath = Path.Combine(playerDir, "Rampages.md");
                    await File.WriteAllLinesAsync(rampagesMdPath, rmd);
                }
            }
            catch { }
        }
    }
}
