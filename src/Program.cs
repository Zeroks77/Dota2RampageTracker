using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using RampageTracker.Core;
using RampageTracker.Data;
using RampageTracker.Processing;

namespace RampageTracker
{
    public class Program
    {
        public static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            // Accept mode via flags first (--full|--parse|--new|--regen-readme);
            // only if absent, accept a positional mode token among the allowed set.
            var mode = "new";
            var allowedModes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "new", "parse", "full", "regen-readme", "pending" };
            if (args.Any(a => string.Equals(a, "--full", StringComparison.OrdinalIgnoreCase))) mode = "full";
            else if (args.Any(a => string.Equals(a, "--parse", StringComparison.OrdinalIgnoreCase))) mode = "parse";
            else if (args.Any(a => string.Equals(a, "--regen-readme", StringComparison.OrdinalIgnoreCase))) mode = "regen-readme";
            else if (args.Any(a => string.Equals(a, "--pending", StringComparison.OrdinalIgnoreCase))) mode = "pending";
            else if (args.Any(a => string.Equals(a, "--new", StringComparison.OrdinalIgnoreCase))) mode = "new";
            else
            {
                var firstNonFlag = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-"))?.ToLowerInvariant();
                if (!string.IsNullOrEmpty(firstNonFlag) && allowedModes.Contains(firstNonFlag))
                {
                    mode = firstNonFlag;
                }
            }

            if (args.Any(a => a is "help" or "--help" or "-h"))
            {
                Console.WriteLine("Usage: dotnet run -- [new|parse|full|regen-readme|pending] [--workers N] [--apikey KEY]");
                Console.WriteLine("       dotnet run -- --regen-readme    # Recommended: always use -- to separate app args");
                Console.WriteLine("       dotnet run -- --pending         # Show queued (and due-now) unparsed matches per player");
                return 0;
            }

            var workersArg = args.SkipWhile(a => a != "--workers").Skip(1).FirstOrDefault();
            int workers = int.TryParse(workersArg, out var w) ? Math.Max(1, w) : -1; // -1 = auto later

            // Init
            var root = Directory.GetCurrentDirectory();
            // Initialize file logging (creates logs/run_*.log and logs/latest.log)
            Logger.Initialize(root, fileNameSuffix: mode);
            var data = new DataManager(root);
            var apiKey = data.GetApiKey();

            // Allow override via --apikey
            var apiIdx = Array.IndexOf(args, "--apikey");
            if (apiIdx >= 0 && apiIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[apiIdx + 1]))
            {
                apiKey = args[apiIdx + 1].Trim();
            }

            Logger.Success($"🚀 Starting Rampage Tracker - Mode: {mode.ToUpper()}, Workers: {workers}, API-Key: {(!string.IsNullOrWhiteSpace(apiKey) ? "✅" : "❌")}");

            var players = (mode == "regen-readme" || mode == "pending") ? await data.GetKnownPlayersAsync() : await data.GetPlayersAsync();
            if (players.Count == 0)
            {
                if (mode == "regen-readme")
                {
                    Logger.Warn("No known players found (neither players.json nor data/*). Nothing to regenerate.");
                    return 0;
                }
                if (mode == "pending")
                {
                    Logger.Warn("No known players found (neither players.json nor data/*). Nothing to report.");
                    return 0;
                }
                Logger.Warn("No players found. Create a players.json at the repository root, e.g.: [169325410,123456789]");
                return 1;
            }

            var usingKey = !string.IsNullOrWhiteSpace(apiKey);
            if (workers <= 0)
            {
                // Pick a dynamic default based on API key and machine
                workers = RateLimiter.GetSuggestedWorkers(usingKey);
            }
            RateLimiter.Initialize(useApiKey: false, maxConcurrency: workers);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RampageTracker/1.0 (+https://opendota.com)");
            ApiKeyUsageTracker? keyTracker = null;
            if (usingKey)
            {
                var usagePath = Path.Combine(root, "data", "apikey-usage.json");
                keyTracker = new ApiKeyUsageTracker(usagePath, threshold: 3000);
            }
            var api = new ApiManager(http, apiKey, keyTracker);
            // Ensure heroes.json exists for README hero icons/slugs (not needed for 'pending' mode)
            if (!string.Equals(mode, "pending", StringComparison.OrdinalIgnoreCase))
            {
                await data.EnsureHeroesAsync(api);
            }

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { cts.Cancel(); } catch { }
                Logger.Warn("⏹️  Graceful shutdown requested (Ctrl+C). All found rampages have been saved automatically!");
                Logger.LogStatistics();
                try { Logger.Close(); } catch { }
            };

            try
            {
                // Migrate any existing per-player ParseQueue.json files into the central GlobalParseQueue once
                try
                {
                    var migrated = await data.MigratePerPlayerQueuesToGlobalAsync(players);
                    if (migrated > 0) Logger.Info($"🧭 Migrated {migrated} parse-queue entries into GlobalParseQueue.json");
                }
                catch (Exception mex)
                {
                    Logger.Warn($"Migration of per-player queues failed: {mex.Message}");
                }

                switch (mode)
                {
                    case "full":
                        Logger.Info("Full Rerun: Clearing data ...");
                        await data.ClearAllAsync();
                        goto case "new";

                    case "new":
                        await Processor.RunNewOnlyAsync(api, data, players, workers, cts.Token);
                        break;

                    case "parse":
                        await Processor.RunParsingOnlyAsync(api, data, players, workers, cts.Token);
                        break;

                    case "pending":
                        {
                            // Resolve data directory (repoRoot/data or repoRoot/src/data) preferring the one that has GlobalParseQueue.json or player folders
                            var candidate1 = Path.Combine(root, "data");
                            var candidate2 = Path.Combine(root, "src", "data");
                            string rootData = candidate1;
                            bool c1HasQueue = File.Exists(Path.Combine(candidate1, "GlobalParseQueue.json"));
                            bool c2HasQueue = File.Exists(Path.Combine(candidate2, "GlobalParseQueue.json"));
                            bool c1HasPlayers = Directory.Exists(candidate1) && Directory.EnumerateDirectories(candidate1).Any();
                            bool c2HasPlayers = Directory.Exists(candidate2) && Directory.EnumerateDirectories(candidate2).Any();
                            if (c2HasQueue || (!c1HasQueue && c2HasPlayers))
                            {
                                rootData = candidate2;
                            }
                            else if (!Directory.Exists(candidate1) && Directory.Exists(candidate2))
                            {
                                rootData = candidate2;
                            }

                            // Load GlobalParseQueue.json directly from the chosen data directory
                            var queuePath = Path.Combine(rootData, "GlobalParseQueue.json");
                            var queue = new List<RampageTracker.Models.ParseQueueEntry>();
                            try
                            {
                                if (File.Exists(queuePath))
                                {
                                    var qjson = await File.ReadAllTextAsync(queuePath);
                                    queue = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RampageTracker.Models.ParseQueueEntry>>(qjson) ?? new List<RampageTracker.Models.ParseQueueEntry>();
                                }
                            }
                            catch { }

                            var allQueued = new HashSet<long>(queue.Select(q => q.MatchId));
                            var now = DateTime.UtcNow;
                            var dueQueued = new HashSet<long>(queue.Where(q => !q.NextCheckAtUtc.HasValue || q.NextCheckAtUtc.Value <= now).Select(q => q.MatchId));

                            int totalAll = 0, totalDue = 0;
                            foreach (var pid in players.OrderBy(p => p))
                            {
                                var pdir = Path.Combine(rootData, pid.ToString());
                                var matchesPath = Path.Combine(pdir, "Matches.json");
                                var countAll = 0; var countDue = 0;
                                try
                                {
                                    if (File.Exists(matchesPath))
                                    {
                                        var json = await File.ReadAllTextAsync(matchesPath);
                                        var ms = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RampageTracker.Models.PlayerMatchSummary>>(json) ?? new List<RampageTracker.Models.PlayerMatchSummary>();
                                        var mine = ms.Select(m => m.MatchId).ToHashSet();
                                        countAll = mine.Count(id => allQueued.Contains(id));
                                        countDue = mine.Count(id => dueQueued.Contains(id));
                                    }
                                }
                                catch { }

                                totalAll += countAll; totalDue += countDue;
                                Console.WriteLine($"Player {pid}: queued={countAll}, due-now={countDue}");
                            }

                            // Optionally, queue entries that don't belong to any known player's Matches.json
                            var allPlayerMatchIds = new HashSet<long>();
                            try
                            {
                                foreach (var pid in players)
                                {
                                    var pdir = Path.Combine(rootData, pid.ToString());
                                    var matchesPath = Path.Combine(pdir, "Matches.json");
                                    if (File.Exists(matchesPath))
                                    {
                                        var json = await File.ReadAllTextAsync(matchesPath);
                                        var ms = Newtonsoft.Json.JsonConvert.DeserializeObject<List<RampageTracker.Models.PlayerMatchSummary>>(json) ?? new List<RampageTracker.Models.PlayerMatchSummary>();
                                        foreach (var m in ms) allPlayerMatchIds.Add(m.MatchId);
                                    }
                                }
                            }
                            catch { }
                            var unassigned = allQueued.Count(id => !allPlayerMatchIds.Contains(id));
                            var unassignedDue = dueQueued.Count(id => !allPlayerMatchIds.Contains(id));

                            Console.WriteLine($"Total queued across players: {totalAll}, due-now: {totalDue}");
                            Console.WriteLine($"Unassigned in queue (not in any known player's Matches.json): total={unassigned}, due-now={unassignedDue}");
                        }
                        break;

                    case "regen-readme":
                        Logger.Info("Regenerating README files from local data only (no parsing, no API calls)...");
                        await Processor.RegenReadmeAsync(data, players);
                        break;

                    default:
                        Logger.Warn($"Unknown mode '{mode}'. Allowed: new | parse | full | regen-readme");
                        return 2;
                }

                Logger.LogStatistics();
                Logger.Success("✅ Processing completed successfully!");
                try { Logger.Close(); } catch { }
                return 0;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("Cancelled.");
                try { Logger.Close(); } catch { }
                return 130;
            }
            catch (Exception ex)
            {
                Logger.Error($"Fatal: {ex.Message}");
                try { Logger.Close(); } catch { }
                return 99;
            }
        }
    }
}
