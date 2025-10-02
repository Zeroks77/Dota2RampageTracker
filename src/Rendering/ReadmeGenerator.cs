using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RampageTracker.Rendering
{
    public static class ReadmeGenerator
    {
        public static async Task UpdateMainAsync(IEnumerable<long> playerIds)
        {
            var root = Directory.GetCurrentDirectory();
            var path = Path.Combine(root, "README.md");
            var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'");
            var lines = new List<string>
            {
                "# Dota 2 Rampage Tracker",
                $"Last updated: {now}",
                "",
                "| PlayerId | Rampages |",
                "|---------:|---------:|"
            };

            foreach (var pid in playerIds)
            {
                var pdir = Path.Combine(root, "data", pid.ToString());
                var rpath = Path.Combine(pdir, "Rampages.json");
                var count = 0;
                if (File.Exists(rpath))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(rpath);
                        var arr = JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
                        count = arr.Count;
                    }
                    catch { }
                }
                lines.Add($"| {pid} | {count} |");
            }

            await File.WriteAllLinesAsync(path, lines);
        }

        public static async Task UpdatePlayerAsync(long playerId, int newFound)
        {
            // Platzhalter: pro-Spieler-Markdown könnte hier gepflegt werden
            await Task.CompletedTask;
        }
    }
}
