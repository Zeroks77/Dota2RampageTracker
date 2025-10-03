using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RampageTracker.Models;

namespace RampageTracker.Data
{
    public class DataManager
    {
        private readonly string _root;
        private readonly string _playersPath;
        private readonly string _apiKeyPath;
        private readonly string _dataDir;
        private Dictionary<string, string>? _env; // cached .env

        public DataManager(string root)
        {
            _root = root;
            _playersPath = Path.Combine(_root, "players.json");
            _apiKeyPath = Path.Combine(_root, "apikey.txt");
            _dataDir = Path.Combine(_root, "data");
            Directory.CreateDirectory(_dataDir);
        }

        // Resolve config files from repo root if running in /src
        private string ResolveConfigPath(string fileName)
        {
            var local = Path.Combine(_root, fileName);
            if (File.Exists(local)) return local;
            var parent = Directory.GetParent(_root)?.FullName;
            if (!string.IsNullOrEmpty(parent))
            {
                var parentCandidate = Path.Combine(parent, fileName);
                if (File.Exists(parentCandidate)) return parentCandidate;
            }
            return local;
        }

        private Dictionary<string, string> LoadEnv()
        {
            if (_env != null) return _env;
            var envPath = ResolveConfigPath(".env");
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(envPath))
            {
                foreach (var raw in File.ReadAllLines(envPath))
                {
                    var line = raw?.Trim();
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("#")) continue;
                    var idx = line.IndexOf('=');
                    if (idx <= 0) continue;
                    var key = line.Substring(0, idx).Trim();
                    var val = line.Substring(idx + 1).Trim();
                    if ((val.StartsWith("\"") && val.EndsWith("\"")) || (val.StartsWith("'") && val.EndsWith("'")))
                    {
                        val = val.Substring(1, Math.Max(0, val.Length - 2));
                    }
                    dict[key] = val;
                }
            }
            _env = dict;
            return dict;
        }

        public async Task<List<long>> GetPlayersAsync()
        {
            // Prefer .env: PLAYERS=comma,separated,ids
            var env = LoadEnv();
            if (env.TryGetValue("PLAYERS", out var playersRaw) && !string.IsNullOrWhiteSpace(playersRaw))
            {
                var list = new List<long>();
                foreach (var part in playersRaw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(part.Trim(), out var id)) list.Add(id);
                }
                return list.Distinct().ToList();
            }

            // Fallback: players.json
            var path = ResolveConfigPath("players.json");
            if (!File.Exists(path)) return new List<long>();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                var ids = JsonConvert.DeserializeObject<List<long>>(json);
                return ids?.Distinct().ToList() ?? new List<long>();
            }
            catch { return new List<long>(); }
        }

        public string? GetApiKey()
        {
            // 1) env var
            var envVar = Environment.GetEnvironmentVariable("OPENDOTA_API_KEY");
            if (!string.IsNullOrWhiteSpace(envVar)) return envVar;

            // 2) .env: API_KEY=...
            var env = LoadEnv();
            if (env.TryGetValue("API_KEY", out var key) && !string.IsNullOrWhiteSpace(key)) return key.Trim();

            // 3) apikey.txt
            var path = ResolveConfigPath("apikey.txt");
            if (File.Exists(path))
            {
                try { return File.ReadAllText(path).Trim(); } catch { }
            }
            return null;
        }

        private string PlayerDir(long playerId)
        {
            var dir = Path.Combine(_dataDir, playerId.ToString());
            Directory.CreateDirectory(dir);
            return dir;
        }

        public long GetLastChecked(long playerId)
        {
            var path = Path.Combine(PlayerDir(playerId), "lastchecked.txt");
            if (!File.Exists(path)) return 0;
            if (long.TryParse(File.ReadAllText(path).Trim(), out var v)) return v;
            return 0;
        }

        public void SetLastChecked(long playerId, long matchId)
        {
            var path = Path.Combine(PlayerDir(playerId), "lastchecked.txt");
            File.WriteAllText(path, matchId.ToString());
        }

        public async Task<List<ParseQueueEntry>> LoadQueueAsync(long playerId)
        {
            var path = Path.Combine(PlayerDir(playerId), "ParseQueue.json");
            if (!File.Exists(path)) return new List<ParseQueueEntry>();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonConvert.DeserializeObject<List<ParseQueueEntry>>(json) ?? new List<ParseQueueEntry>();
            }
            catch { return new List<ParseQueueEntry>(); }
        }

        public async Task SaveQueueAsync(long playerId, List<ParseQueueEntry> entries)
        {
            var path = Path.Combine(PlayerDir(playerId), "ParseQueue.json");
            var json = JsonConvert.SerializeObject(entries.OrderBy(e => e.NextCheckAtUtc ?? DateTime.MinValue), Formatting.Indented);
            await File.WriteAllTextAsync(path, json);
        }

        public async Task AppendRampagesAsync(long playerId, List<long> matchIds)
        {
            var path = Path.Combine(PlayerDir(playerId), "Rampages.json");
            var existing = new HashSet<long>();
            if (File.Exists(path))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    existing = JsonConvert.DeserializeObject<HashSet<long>>(json) ?? new HashSet<long>();
                }
                catch { }
            }
            foreach (var id in matchIds) existing.Add(id);
            await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(existing.OrderBy(x => x), Formatting.Indented));
        }

        public async Task ClearAllAsync()
        {
            if (Directory.Exists(_dataDir))
            {
                await Task.Run(() => Directory.Delete(_dataDir, true));
            }
            Directory.CreateDirectory(_dataDir);
        }
    }
}
