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

            // Args
            var mode = args.FirstOrDefault()?.ToLowerInvariant() ?? "new"; // new | parse | full
            if (mode == "help" || mode == "--help" || mode == "-h")
            {
                Console.WriteLine("Usage: dotnet run -- [new|parse|full] [--workers N] [--apikey KEY]");
                return 0;
            }
            var workersArg = args.SkipWhile(a => a != "--workers").Skip(1).FirstOrDefault();
            int workers = int.TryParse(workersArg, out var w) ? Math.Max(1, w) : 32;

            // Init
            Logger.Info($"Start mode={mode} workers={workers}");
            var root = Directory.GetCurrentDirectory();
            var data = new DataManager(root);
            var apiKey = data.GetApiKey();

            // Allow override via --apikey
            var apiIdx = Array.IndexOf(args, "--apikey");
            if (apiIdx >= 0 && apiIdx + 1 < args.Length && !string.IsNullOrWhiteSpace(args[apiIdx + 1]))
            {
                apiKey = args[apiIdx + 1].Trim();
            }

            var players = await data.GetPlayersAsync();
            if (players.Count == 0)
            {
                Logger.Warn("Keine Spieler gefunden. Lege players.json im Projekt-Root an, z.B.: [169325410,123456789]");
                return 1;
            }

            RateLimiter.Initialize(useApiKey: !string.IsNullOrWhiteSpace(apiKey));
            using var http = new HttpClient();
            // Optional: identify client a bit nicer
            http.DefaultRequestHeaders.UserAgent.ParseAdd("RampageTracker/1.0 (+https://opendota.com)");
            var api = new ApiManager(http, apiKey);

            var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                try { cts.Cancel(); } catch { }
                Logger.Warn("Abbruch angefordert (Ctrl+C). Beende laufenden Vorgang sauber...");
            };

            try
            {
                switch (mode)
                {
                    case "full":
                        Logger.Info("Full Rerun: Lösche Daten ...");
                        await data.ClearAllAsync();
                        goto case "new";

                    case "new":
                        await Processor.RunNewOnlyAsync(api, data, players, workers, cts.Token);
                        break;

                    case "parse":
                        await Processor.RunParsingOnlyAsync(api, data, players, workers, cts.Token);
                        break;

                    default:
                        Logger.Warn($"Unbekannter Modus '{mode}'. Erlaubt: new | parse | full");
                        return 2;
                }

                Logger.Info("Fertig.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                Logger.Warn("Abgebrochen.");
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
