using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RampageTracker.Core;
using RampageTracker.Models;

namespace RampageTracker.Data
{
    public class DataManager
    {
        private readonly string _root;
        private readonly string _playersPath;
        private readonly string _apiKeyPath;
        private readonly string _dataDir;
        private readonly string _globalQueuePath;
        private Dictionary<string, string>? _env; // cached .env
        
        // File locks for concurrent access protection
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new();
        
        private static SemaphoreSlim GetFileLock(string filePath)
        {
            return _fileLocks.GetOrAdd(filePath, _ => new SemaphoreSlim(1, 1));
        }

        public DataManager(string root)
        {
            _root = root;
            _playersPath = Path.Combine(_root, "players.json");
            _apiKeyPath = Path.Combine(_root, "apikey.txt");
            _dataDir = Path.Combine(_root, "data");
            _globalQueuePath = Path.Combine(_dataDir, "GlobalParseQueue.json");
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

        // Returns players from config; if none found, falls back to discovering from existing data folders.
        public async Task<List<long>> GetKnownPlayersAsync()
        {
            var list = await GetPlayersAsync();
            if (list.Count > 0) return list;
            try
            {
                if (Directory.Exists(_dataDir))
                {
                    var ids = new List<long>();
                    foreach (var dir in Directory.EnumerateDirectories(_dataDir))
                    {
                        var name = Path.GetFileName(dir);
                        if (long.TryParse(name, out var id)) ids.Add(id);
                    }
                    return ids.Distinct().OrderBy(x => x).ToList();
                }
            }
            catch { }
            return new List<long>();
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

        public async Task<long> GetLastCheckedAsync(long playerId)
        {
            var path = Path.Combine(PlayerDir(playerId), "lastchecked.txt");
            var fileLock = GetFileLock(path);
            
            await fileLock.WaitAsync();
            try
            {
                if (!File.Exists(path)) return 0;
                var content = await File.ReadAllTextAsync(path);
                if (long.TryParse(content.Trim(), out var v)) return v;
                return 0;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetLastCheckedAsync(long playerId, long matchId)
        {
            var path = Path.Combine(PlayerDir(playerId), "lastchecked.txt");
            var fileLock = GetFileLock(path);
            
            await fileLock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(path, matchId.ToString());
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task<List<ParseQueueEntry>> LoadQueueAsync(long playerId)
        {
            var path = Path.Combine(PlayerDir(playerId), "ParseQueue.json");
            if (!File.Exists(path)) return new List<ParseQueueEntry>();
            
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonConvert.DeserializeObject<List<ParseQueueEntry>>(json) ?? new List<ParseQueueEntry>();
            }
            catch 
            { 
                return new List<ParseQueueEntry>(); 
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SaveQueueAsync(long playerId, List<ParseQueueEntry> entries)
        {
            var path = Path.Combine(PlayerDir(playerId), "ParseQueue.json");
            var json = JsonConvert.SerializeObject(entries.OrderBy(e => e.NextCheckAtUtc ?? DateTime.MinValue), Formatting.Indented);
            
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(path, json);
                Logger.Debug($"📁 Saved ParseQueue for player {playerId} ({entries.Count} entries)");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task AppendRampageAsync(long playerId, RampageEntry rampage)
        {
            var path = Path.Combine(PlayerDir(playerId), "Rampages.json");
            var fileLock = GetFileLock(path);
            
            await fileLock.WaitAsync();
            try
            {
                var existing = new List<RampageEntry>();
                
                // Load existing rampages
                if (File.Exists(path))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(path);
                        // Try new format first
                        try
                        {
                            existing = JsonConvert.DeserializeObject<List<RampageEntry>>(json) ?? new List<RampageEntry>();
                        }
                        catch
                        {
                            // Fallback: convert old format (just match IDs) to new format
                            var oldFormat = JsonConvert.DeserializeObject<List<long>>(json) ?? new List<long>();
                            existing = oldFormat.Select(id => new RampageEntry 
                            { 
                                MatchId = id, 
                                HeroName = "Unknown", 
                                MatchDate = null 
                            }).ToList();
                        }
                    }
                    catch { }
                }
                
                // Check if rampage already exists
                if (!existing.Any(r => r.MatchId == rampage.MatchId))
                {
                    existing.Add(rampage);
                    existing = existing.OrderBy(r => r.StartTime ?? 0).ToList();
                    
                    await File.WriteAllTextAsync(path, JsonConvert.SerializeObject(existing, Formatting.Indented));
                    Logger.Success($"💾 Rampage {rampage.MatchId} ({rampage.HeroName}) saved for player {playerId}");
                }
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SavePlayerMatchesAsync(long playerId, IEnumerable<PlayerMatchSummary> matches)
        {
            var path = Path.Combine(PlayerDir(playerId), "Matches.json");
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                var json = JsonConvert.SerializeObject(matches ?? Enumerable.Empty<PlayerMatchSummary>(), Formatting.Indented);
                await File.WriteAllTextAsync(path, json);
                Logger.Debug($"💾 Saved {playerId} Matches.json");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SavePlayerProfileAsync(long playerId, PlayerProfile profile)
        {
            var path = Path.Combine(PlayerDir(playerId), "profile.json");
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                var json = JsonConvert.SerializeObject(profile, Formatting.Indented);
                await File.WriteAllTextAsync(path, json);
                Logger.Debug($"💾 Saved {playerId} profile.json");
            }
            finally
            {
                fileLock.Release();
            }
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

        // --------------- Global Parse Queue (centralized across players) ---------------
        public async Task<List<ParseQueueEntry>> LoadGlobalQueueAsync()
        {
            var path = _globalQueuePath;
            if (!File.Exists(path)) return new List<ParseQueueEntry>();
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonConvert.DeserializeObject<List<ParseQueueEntry>>(json) ?? new List<ParseQueueEntry>();
            }
            catch
            {
                return new List<ParseQueueEntry>();
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SaveGlobalQueueAsync(List<ParseQueueEntry> entries)
        {
            var path = _globalQueuePath;
            var json = JsonConvert.SerializeObject(entries
                .GroupBy(e => e.MatchId)
                .Select(g => g.OrderByDescending(x => x.JobId.HasValue).ThenBy(x => x.Tries).First())
                .OrderBy(e => e.NextCheckAtUtc ?? DateTime.MinValue), Formatting.Indented);
            var fileLock = GetFileLock(path);
            await fileLock.WaitAsync();
            try
            {
                await File.WriteAllTextAsync(path, json);
                Logger.Debug($"📁 Saved GlobalParseQueue ({entries.Count} entries)");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task EnqueueGlobalParseAsync(long matchId, long? jobId, int tries, DateTime nextCheck)
        {
            var queue = await LoadGlobalQueueAsync();
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
                existing.JobId = jobId ?? existing.JobId;
                existing.Tries = Math.Max(existing.Tries, tries);
                existing.NextCheckAtUtc = nextCheck;
            }
            await SaveGlobalQueueAsync(queue);
        }

        public async Task<(ParseQueueEntry? Next, List<ParseQueueEntry> Remainder)> DequeueDueGlobalAsync(DateTime nowUtc)
        {
            var queue = await LoadGlobalQueueAsync();
            var next = queue.Where(e => !e.NextCheckAtUtc.HasValue || e.NextCheckAtUtc.Value <= nowUtc)
                            .OrderBy(e => e.NextCheckAtUtc ?? DateTime.MinValue)
                            .FirstOrDefault();
            if (next != null)
            {
                queue.Remove(next);
                await SaveGlobalQueueAsync(queue);
            }
            return (next, queue);
        }

        // One-time migration helper: merge per-player ParseQueue.json into global queue
        public async Task<int> MigratePerPlayerQueuesToGlobalAsync(IEnumerable<long> players)
        {
            // If global already has entries, skip migration
            var global = await LoadGlobalQueueAsync();
            if (global.Count > 0) return 0;

            var merged = new Dictionary<long, ParseQueueEntry>();
            foreach (var pid in players.Distinct())
            {
                var path = Path.Combine(PlayerDir(pid), "ParseQueue.json");
                if (!File.Exists(path)) continue;

                List<ParseQueueEntry> entries;
                var fileLock = GetFileLock(path);
                await fileLock.WaitAsync();
                try
                {
                    var json = await File.ReadAllTextAsync(path);
                    entries = JsonConvert.DeserializeObject<List<ParseQueueEntry>>(json) ?? new List<ParseQueueEntry>();
                }
                catch
                {
                    entries = new List<ParseQueueEntry>();
                }
                finally
                {
                    fileLock.Release();
                }

                foreach (var e in entries)
                {
                    if (!merged.TryGetValue(e.MatchId, out var existing))
                    {
                        merged[e.MatchId] = e;
                    }
                    else
                    {
                        // Prefer entries with JobId, then fewer tries, then earlier NextCheck
                        var candidate = new[] { existing, e }
                            .OrderByDescending(x => x.JobId.HasValue)
                            .ThenBy(x => x.Tries)
                            .ThenBy(x => x.NextCheckAtUtc ?? DateTime.MinValue)
                            .First();
                        merged[e.MatchId] = candidate;
                    }
                }

                // Optionally rename old file to avoid repeated migrations
                try
                {
                    var bak = Path.Combine(PlayerDir(pid), "ParseQueue.migrated.json");
                    if (File.Exists(path)) File.Move(path, bak, overwrite: true);
                }
                catch { }
            }

            if (merged.Count > 0)
            {
                await SaveGlobalQueueAsync(merged.Values.ToList());
            }
            return merged.Count;
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
