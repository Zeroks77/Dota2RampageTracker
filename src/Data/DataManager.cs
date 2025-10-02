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

        public DataManager(string root)
        {
            _root = root;
            _playersPath = Path.Combine(_root, "players.json");
            _apiKeyPath = Path.Combine(_root, "apikey.txt");
            _dataDir = Path.Combine(_root, "data");
            Directory.CreateDirectory(_dataDir);
        }

        public async Task<List<long>> GetPlayersAsync()
        {
            if (!File.Exists(_playersPath)) return new List<long>();
            try
            {
                var json = await File.ReadAllTextAsync(_playersPath);
                var ids = JsonConvert.DeserializeObject<List<long>>(json);
                return ids?.Distinct().ToList() ?? new List<long>();
            }
            catch { return new List<long>(); }
        }

        public string? GetApiKey()
        {
            var env = Environment.GetEnvironmentVariable("OPENDOTA_API_KEY");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            if (File.Exists(_apiKeyPath))
            {
                try { return File.ReadAllText(_apiKeyPath).Trim(); } catch { }
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
