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

            // Accept mode flags anywhere: --full | --parse | --new | --regen-readme or positional "full|parse|new|regen-readme"
            var mode = "new";
            var firstNonFlag = args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && !a.StartsWith("-"))?.ToLowerInvariant();
            if (!string.IsNullOrEmpty(firstNonFlag)) mode = firstNonFlag;
            else if (args.Any(a => string.Equals(a, "--full", StringComparison.OrdinalIgnoreCase))) mode = "full";
            else if (args.Any(a => string.Equals(a, "--parse", StringComparison.OrdinalIgnoreCase))) mode = "parse";
            else if (args.Any(a => string.Equals(a, "--new", StringComparison.OrdinalIgnoreCase))) mode = "new";
            else if (args.Any(a => string.Equals(a, "--regen-readme", StringComparison.OrdinalIgnoreCase))) mode = "regen-readme";

            if (args.Any(a => a is "help" or "--help" or "-h"))
            {
                Console.WriteLine("Usage: dotnet run -- [new|parse|full|regen-readme] [--workers N] [--apikey KEY]");
                return 0;
            }

            var workersArg = args.SkipWhile(a => a != "--workers").Skip(1).FirstOrDefault();
            int workers = int.TryParse(workersArg, out var w) ? Math.Max(1, w) : -1; // -1 = auto later

            // Init
            var root = Directory.GetCurrentDirectory();
            var data = new DataManager(root);
            var apiKey = data.GetApiKey();

            // Allow override via --apikey
            var apiIdx = Array.IndexOf(args, "--apikey");
            if (apiIdx >= 0 && apiIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[apiIdx + 1]))
            {
                apiKey = args[apiIdx + 1].Trim();
            }

            Logger.Success($"🚀 Starting Rampage Tracker - Mode: {mode.ToUpper()}, Workers: {workers}, API-Key: {(!string.IsNullOrWhiteSpace(apiKey) ? "✅" : "❌")}");

            var players = mode == "regen-readme" ? await data.GetKnownPlayersAsync() : await data.GetPlayersAsync();
            if (players.Count == 0)
            {
                if (mode == "regen-readme")
                {
                    Logger.Warn("No known players found (neither players.json nor data/*). Nothing to regenerate.");
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
            RateLimiter.Initialize(useApiKey: usingKey, maxConcurrency: workers);
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RampageTracker/1.0 (+https://opendota.com)");
            var api = new ApiManager(http, apiKey);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { cts.Cancel(); } catch { }
                Logger.Warn("⏹️  Graceful shutdown requested (Ctrl+C). All found rampages have been saved automatically!");
                Logger.LogStatistics();
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
                return 0;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("Cancelled.");
                return 130;
            }
            catch (Exception ex)
            {
                Logger.Error($"Fatal: {ex.Message}");
                return 99;
            }
        }
    }
}
