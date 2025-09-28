using System.Diagnostics;
using System.Net;
using dotenv.net;
using Newtonsoft.Json;
using OpenDotaRampage.Helpers;
using OpenDotaRampage.Models;

class Program
{
    // Resolve repo root and always write into the repo-level Players folder
    public static readonly string outputDirectory = ResolveOutputDirectory();
    public static readonly HttpClient client = CreateHttpClient();
    public static string? apiKey;
    public static readonly Stopwatch stopwatch = new Stopwatch();
    private static List<long> players = new List<long>();
    private static Dictionary<int, Hero> heroData = new Dictionary<int, Hero>();
   private static Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)> steamProfiles = new Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)>();
    private static Dictionary<long, string> playerDirectoryMapping = new Dictionary<long, string>();
    private static readonly string mappingFilePath = Path.Combine(Directory.GetParent(outputDirectory)!.FullName, "player_directory_mapping.json");
    private static CancellationTokenSource? heartbeatCts;
    private static Task? heartbeatTask;

    static async Task Main(string[] args)
    {
        // Load environment variables from .env file
        DotEnv.Load();

        // Logging configuration (CLI)
        var logLevelArg = args.FirstOrDefault(a => a.StartsWith("--log-level="));
        var logJson = args.Any(a => a.Equals("--log-json", StringComparison.OrdinalIgnoreCase));
        var logFileArg = args.FirstOrDefault(a => a.StartsWith("--log-file="));
        var runIdArg = args.FirstOrDefault(a => a.StartsWith("--run-id="));
    var verboseMatchProcessor = args.Any(a => a.Equals("--verbose-parse", StringComparison.OrdinalIgnoreCase));
    var pendingParallelArg = args.FirstOrDefault(a => a.StartsWith("--pending-parallel="));
    var parseWorkersArg = args.FirstOrDefault(a => a.StartsWith("--parse-workers="));
    var parsePollArg = args.FirstOrDefault(a => a.StartsWith("--parse-poll-seconds="));
    var parseAttemptsArg = args.FirstOrDefault(a => a.StartsWith("--parse-attempts="));
    bool waitUntilDrained = args.Any(a => a.Equals("--wait-until-drained", StringComparison.OrdinalIgnoreCase));
        var level = Logger.LogLevel.Info;
        if (!string.IsNullOrEmpty(logLevelArg))
        {
            var v = logLevelArg.Substring("--log-level=".Length).ToLowerInvariant();
            if (v == "debug") level = Logger.LogLevel.Debug;
            else if (v == "info") level = Logger.LogLevel.Info;
            else if (v == "warn" || v == "warning") level = Logger.LogLevel.Warn;
            else if (v == "error") level = Logger.LogLevel.Error;
        }
        string? logFile = null;
        if (!string.IsNullOrEmpty(logFileArg)) logFile = logFileArg.Substring("--log-file=".Length);
        string? runId = null;
        if (!string.IsNullOrEmpty(runIdArg)) runId = runIdArg.Substring("--run-id=".Length);
        Logger.Init(logFile, level, json: logJson, runId: runId);
    MatchProcessor.VerboseLogging = verboseMatchProcessor || level == Logger.LogLevel.Debug;
    if (!string.IsNullOrEmpty(pendingParallelArg))
    {
        var s = pendingParallelArg.Substring("--pending-parallel=".Length);
        if (int.TryParse(s, out var pp) && pp > 0)
        {
            MatchProcessor.PendingScanParallelism = pp;
            Logger.Info("pending scan parallelism configured", ctx: new Dictionary<string, object?> { {"parallelism", pp } });
        }
        else
        {
            Logger.Warn("invalid --pending-parallel value, keeping default", ctx: new Dictionary<string, object?> { {"arg", s }, {"default", MatchProcessor.PendingScanParallelism } });
        }
    }
    if (!string.IsNullOrEmpty(parseWorkersArg))
    {
        var s = parseWorkersArg.Substring("--parse-workers=".Length);
        if (int.TryParse(s, out var pw) && pw > 0)
        {
            MatchProcessor.DefaultParseWorkers = pw;
            Logger.Info("parse workers configured", ctx: new Dictionary<string, object?> { {"workers", pw } });
        }
        else
        {
            Logger.Warn("invalid --parse-workers value, keeping default", ctx: new Dictionary<string, object?> { {"arg", s }, {"default", MatchProcessor.DefaultParseWorkers } });
        }
    }
    if (!string.IsNullOrEmpty(parsePollArg))
    {
        var s = parsePollArg.Substring("--parse-poll-seconds=".Length);
        if (int.TryParse(s, out var secs) && secs >= 5 && secs <= 3600)
        {
            MatchProcessor.ParsePollDelaySeconds = secs;
            Logger.Info("parse poll delay configured", ctx: new Dictionary<string, object?> { {"seconds", secs } });
        }
        else
        {
            Logger.Warn("invalid --parse-poll-seconds value, keeping default", ctx: new Dictionary<string, object?> { {"arg", s }, {"default", MatchProcessor.ParsePollDelaySeconds } });
        }
    }
    if (!string.IsNullOrEmpty(parseAttemptsArg))
    {
        var s = parseAttemptsArg.Substring("--parse-attempts=".Length);
        if (int.TryParse(s, out var attempts) && attempts >= 1 && attempts <= 10)
        {
            MatchProcessor.ParseAttemptsPerWorker = attempts;
            Logger.Info("parse attempts per worker configured", ctx: new Dictionary<string, object?> { {"attempts", attempts } });
        }
        else
        {
            Logger.Warn("invalid --parse-attempts value, keeping default", ctx: new Dictionary<string, object?> { {"arg", s }, {"default", MatchProcessor.ParseAttemptsPerWorker } });
        }
    }

        // Initialize the rate limit reset timer
        RateLimiter.Initialize();

        // Start the stopwatch to track total time spent
        stopwatch.Start();

        // Check if the API is alive
        //if (!await IsApiAlive())
        //{
        //    Console.WriteLine("The OpenDota API is currently unavailable. Please try again later.");
        //    return;
        //}

    // Load configuration from environment variables
    apiKey = Environment.GetEnvironmentVariable("API_KEY");
    var playersCsv = Environment.GetEnvironmentVariable("PLAYERS");
    // Ensure rate limiter reflects API key availability for all modes (including pending-only)
    var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
    RateLimiter.SetRateLimit(hasApiKey); // 1000/min with API key, 60/min otherwise
    Logger.Info("rate limiter configured", ctx: new Dictionary<string, object?> { {"hasApiKey", hasApiKey }, {"maxPerMinute", hasApiKey ? 1000 : 60 } });

    // CLI overrides: --players=... and --regen-readme-only and --revalidate-cache and --full-rescan and --force-parse
    bool regenReadmeOnly = args.Any(a => a.Equals("--regen-readme-only", StringComparison.OrdinalIgnoreCase));
    bool revalidateCache = args.Any(a => a.Equals("--revalidate-cache", StringComparison.OrdinalIgnoreCase));
    bool forceParse = args.Any(a => a.Equals("--force-parse", StringComparison.OrdinalIgnoreCase));
    bool fullRescan = args.Any(a => a.Equals("--full-rescan", StringComparison.OrdinalIgnoreCase));
    bool pendingOnly = args.Any(a => a.Equals("--pending-only", StringComparison.OrdinalIgnoreCase));
    bool scanAllPending = args.Any(a => a.Equals("--scan-all-pending", StringComparison.OrdinalIgnoreCase));
    int parseRunMinutes = 3;
    var parseRunStr = args.FirstOrDefault(a => a.StartsWith("--parse-run-minutes="));
    if (!string.IsNullOrEmpty(parseRunStr))
    {
        var val = parseRunStr.Substring("--parse-run-minutes=".Length);
        if (int.TryParse(val, out var parsed) && parsed >= 0 && parsed <= 60)
        {
            parseRunMinutes = parsed;
        }
    }
        var argPlayers = args.FirstOrDefault(a => a.StartsWith("--players="));
        var argPlayer = args.FirstOrDefault(a => a.StartsWith("--player="));
        if (!string.IsNullOrEmpty(argPlayers))
        {
            playersCsv = argPlayers.Substring("--players=".Length);
        }
        // Convenience alias: --player=ID to process exactly one player
        if (!string.IsNullOrEmpty(argPlayer))
        {
            playersCsv = argPlayer.Substring("--player=".Length);
            Logger.Info("single-player override active", ctx: new Dictionary<string, object?> { {"player", playersCsv } });
        }

        // Check if the required environment variables exist
        if (string.IsNullOrEmpty(playersCsv))
        {
            Console.WriteLine("Error: PLAYERS environment variable is missing.");
            return;
        }

    // Parse the players CSV into a list of long
    players = playersCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(long.Parse).ToList();
    Logger.Info($"Loaded PLAYERS: {string.Join(",", players)}");

        // Load player directory mapping from JSON file
        LoadPlayerDirectoryMapping();

        // Skip metadata fetches entirely in pending-only mode to start scanning faster

        // Fetch hero data unless we only want to regenerate README from cache (and not in pending-only)
        if (!pendingOnly && !regenReadmeOnly)
        {
            heroData = await HeroDataFetcher.FetchHeroData(client);
        }

        // Start background parse workers only if not in pending-only mode
        if (!pendingOnly)
        {
            MatchProcessor.StartParseBackground(client, workers: MatchProcessor.DefaultParseWorkers);
            Logger.Info($"Parse workers started", ctx: new Dictionary<string, object?> { {"workers", MatchProcessor.DefaultParseWorkers } });
            heartbeatCts = new CancellationTokenSource();
            heartbeatTask = Task.Run(() => HeartbeatLoop(TimeSpan.FromSeconds(30), heartbeatCts.Token));
        }

        // Pending-only mode (passive): do not enqueue or request parses; only check status/details and clean up
        if (pendingOnly)
        {
            Logger.Info($"Pending-only (passive) mode: checking pending matches for {parseRunMinutes} minute(s) without sending new parse requests...{(scanAllPending ? " (full scan this cycle)" : string.Empty)}");
            // Bounded parallelism across players to hide latency but keep global RL
            var semPlayers = new SemaphoreSlim(Math.Min(4, Math.Max(1, players.Count)));
            var tasks = new List<Task>();
            foreach (var pid in players)
            {
                await semPlayers.WaitAsync();
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var recovered = await MatchProcessor.ProcessPendingParses(client, pid, passive: true, scanAll: scanAllPending);
                        var remaining = MatchProcessor.GetPendingCount(pid);
                        Logger.Info($"pending-only sweep", ctx: new Dictionary<string, object?> { {"player", pid }, {"recovered", recovered.Count }, {"remaining", remaining } });
                    }
                    finally { semPlayers.Release(); }
                }));
            }
            await Task.WhenAll(tasks);

            if (parseRunMinutes > 0)
            {
                var end = DateTime.UtcNow.AddMinutes(parseRunMinutes);
                while (DateTime.UtcNow < end)
                {
                    int totalPending = 0;
                    semPlayers = new SemaphoreSlim(Math.Min(4, Math.Max(1, players.Count)));
                    tasks.Clear();
                    foreach (var pid in players)
                    {
                        await semPlayers.WaitAsync();
                        tasks.Add(Task.Run(async () =>
                        {
                            try { await MatchProcessor.ProcessPendingParses(client, pid, passive: true, scanAll: false); }
                            finally { semPlayers.Release(); }
                        }));
                    }
                    await Task.WhenAll(tasks);
                    foreach (var pid in players) totalPending += MatchProcessor.GetPendingCount(pid);
                    Logger.Info("pending-only progress", ctx: new Dictionary<string, object?> { {"totalPending", totalPending }, {"players", players.Count }, {"secondsLeft", (int)(end - DateTime.UtcNow).TotalSeconds } });
                    try { await Task.Delay(TimeSpan.FromSeconds(60)); } catch { break; }
                }
            }
            MatchProcessor.StopParseBackground();
            var md = MatchProcessor.MetricsDetailed();
            Logger.Info("Pending-only run finished", ctx: new Dictionary<string, object?> {
                {"parseRequests", md.parseRequests }, {"recovered", md.recovered }, {"dropped", md.dropped }, {"pollChecks", md.pollChecks }, {"enqueued", md.enqueued }, {"resolvedViaStatus", md.resolvedViaStatus }, {"resolvedViaDetails", md.resolvedViaDetails }
            });

            // Per-player pending summary (stats view)
            foreach (var pid in players)
            {
                var pendingList = MatchProcessor.GetPendingList(pid);
                var attempts = MatchProcessor.GetAttemptsSnapshot(pid);
                var meta = MatchProcessor.GetPendingMetaSnapshot(pid);
                var sample = pendingList.Take(10).ToList();
                var maxAttempts = pendingList.Count == 0 ? 0 : pendingList.Max(id => attempts.TryGetValue(id, out var n) ? n : 0);
                var oldestTs = pendingList.Count == 0 ? (long?)null : pendingList.Select(id => meta.TryGetValue(id, out var ts) ? ts : 0L).DefaultIfEmpty(0).Min();
                Logger.Info("pending summary", ctx: new Dictionary<string, object?> {
                    {"player", pid },
                    {"count", pendingList.Count },
                    {"maxAttempts", maxAttempts },
                    {"oldestLastChecked", oldestTs },
                    {"sampleMatchIds", sample }
                });
            }
            return;
        }

    // Determine the total number of new matches for all players
        int totalNewMatches = 0;
        var playerMatches = new Dictionary<long, List<Match>>();
    foreach (var playerId in players)
        {
            // Fetch player's Steam name, avatar URL, and totals data
            string steamName = $"Player {playerId}";
            string avatarUrl = string.Empty;
            Dictionary<string,int> totals = new();
            CountsResponse counts;
            try
            {
                var profile = await HeroDataFetcher.GetPlayerSteamName(client, playerId);
                steamName = profile.SteamName;
                avatarUrl = profile.AvatarUrl;
            }
            catch (Exception ex)
            {
                Logger.Warn($"steam profile fetch failed: {ex.Message}", ctx: new Dictionary<string, object?> { {"player", playerId } });
            }
            try
            {
                totals = await HeroDataFetcher.GetPlayerTotals(client, playerId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"totals fetch failed: {ex.Message}", ctx: new Dictionary<string, object?> { {"player", playerId } });
            }
            try
            {
                counts = await HeroDataFetcher.GetPlayerCounts(client, playerId);
            }
            catch (Exception ex)
            {
                Logger.Warn($"counts fetch failed: {ex.Message}", ctx: new Dictionary<string, object?> { {"player", playerId } });
                counts = new CountsResponse
                {
                    GameMode = new Dictionary<string, WinLoss>(),
                    LobbyType = new Dictionary<string, WinLoss>(),
                    LaneRole = new Dictionary<string, WinLoss>(),
                    Region = new Dictionary<string, WinLoss>(),
                    Patch = new Dictionary<string, WinLoss>(),
                    IsRadiant = new Dictionary<string, WinLoss>(),
                    LeaverStatus = new Dictionary<string, WinLoss>()
                };
            }
            steamProfiles[playerId.ToString()] = (steamName, avatarUrl, totals, counts);

            // Check if the directory name has changed
            if (playerDirectoryMapping.TryGetValue(playerId, out var oldDirectoryName))
            {
                // Check if the directory name has changed
            string oldDirectoryPath = Path.Combine(outputDirectory, oldDirectoryName.ToString());
            string newDirectoryPath = Path.Combine(outputDirectory, playerId.ToString());
                if (Directory.Exists(oldDirectoryPath) && oldDirectoryPath != newDirectoryPath)
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    playerDirectoryMapping[playerId] = playerId.ToString();
                }
            }
            else
            {
                playerDirectoryMapping[playerId] = steamName;
            }

            if (!regenReadmeOnly)
            {
                long lastCheckedMatchId = fullRescan ? 0 : MatchProcessor.ReadLastCheckedMatchId(playerId.ToString());
                var matches = await MatchProcessor.GetPlayerMatches(client, playerId, lastCheckedMatchId);
                int playerMatchesCount = matches.Count();
                totalNewMatches += playerMatchesCount;
                playerMatches[playerId] = matches.ToList();
                Logger.Info("player matches", ctx: new Dictionary<string, object?> { {"player", playerId }, {"steamName", steamName }, {"newMatches", playerMatchesCount } });
                ProgressTracker.SetTotalNewMatches(playerId, playerMatchesCount);
            }
        }

        // Save updated player directory mapping to JSON file
        SavePlayerDirectoryMapping();

    Logger.Info($"Total New Matches: {totalNewMatches}");

    // (Already set earlier so pending-only benefits too)

        // If API degraded (circuit open), we skip fetching and only regenerate markdown from cache
        if (OpenDotaRampage.Helpers.ApiHelper.IsDegradedMode)
        {
            Logger.Warn("API appears degraded (circuit open). Falling back to cache-only README regeneration.");
            regenReadmeOnly = true;
        }

        // Optionally revalidate cache-only (request parses for missing/obsolete matches)
        if (revalidateCache)
        {
            Logger.Info("Revalidating cached rampages for players (will request parse if missing)...");
            foreach (var pid in players)
            {
                var missing = await MatchProcessor.RevalidateCachedRampages(client, pid);
                Logger.Info("revalidate cache", ctx: new Dictionary<string, object?> { {"player", pid }, {"missing", missing.Count } });
            }
        }

        if (forceParse)
        {
            Logger.Info("Force-parsing all cached rampage matches for players...");
            foreach (var pid in players)
            {
                var submitted = await MatchProcessor.ForceParseCachedRampages(client, pid);
                Logger.Info("force-parse cached", ctx: new Dictionary<string, object?> { {"player", pid }, {"submitted", submitted } });
            }
        }

        // Process each player
        foreach (var playerId in players)
        {
            // Fetch player's Steam name, avatar URL, and totals data
            var (steamName, avatarUrl, totals, counts) = steamProfiles[playerId.ToString()];
            var cachedRampageMatches = MarkdownGenerator.LoadRampageMatchesFromCache(playerId.ToString());
            List<Match> allRampageMatches;

            if (!regenReadmeOnly)
            {
                var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, playerMatches[playerId], updateWatermark: !fullRescan);
                allRampageMatches = rampageMatches.Concat(cachedRampageMatches)
                    .GroupBy(m => m.MatchId)
                    .Select(g => g.First())
                    .ToList();
                allRampageMatches = await MatchProcessor.EnrichStartTimes(client, allRampageMatches);
                MatchProcessor.WriteRampageCache(playerId.ToString(), allRampageMatches);
            }
            else
            {
                allRampageMatches = cachedRampageMatches;
            }

            steamProfiles[playerId.ToString()].Totals["rampages"] = allRampageMatches.Count;
            MarkdownGenerator.GenerateMarkdown(steamName, allRampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file (skip in pending-only mode where metadata wasn't fetched)
        if (!pendingOnly)
        {
            var gameModes = await HeroDataFetcher.GetGameModes(client);
            var lobbyTypes = await HeroDataFetcher.GetLobbyTypes(client);
            var patches = await HeroDataFetcher.GetPatches(client);
            MarkdownGenerator.GenerateMainReadme(steamProfiles, gameModes, lobbyTypes, patches);
        }

        // In active mode, optionally wait until the parse queue is drained (no time limit)
        if (!pendingOnly && waitUntilDrained)
        {
            while (true)
            {
                var (queued, workers) = MatchProcessor.ParseQueueSnapshot();
                if (queued <= 0)
                {
                    Logger.Info("parse queue drained; continuing to shutdown", ctx: new Dictionary<string, object?> { {"workers", workers } });
                    break;
                }
                Logger.Info("parse run in progress", ctx: new Dictionary<string, object?> { {"queue", queued }, {"workers", workers } });
                try { await Task.Delay(TimeSpan.FromSeconds(30)); } catch { break; }
            }
        }

        // Stop the stopwatch
        stopwatch.Stop();
    var (ok, r429, r5xx, bytes, avgLatency, requests) = ApiHelper.Metrics.Snapshot();
    var (waits, totalDelay) = RateLimiter.Snapshot();
    var md2 = MatchProcessor.MetricsDetailed();
    Logger.Info("run summary", ctx: new Dictionary<string, object?> {
        {"http.requests", requests }, {"http.ok", ok }, {"http.429", r429 }, {"http.5xx", r5xx }, {"http.avgLatencyMs", avgLatency }, {"http.kb", Math.Round(bytes/1024.0,1) },
        {"rl.waits", waits }, {"rl.totalDelaySec", Math.Round(totalDelay.TotalSeconds,1) }, {"runtime", stopwatch.Elapsed.ToString() },
        {"parse.parseRequests", md2.parseRequests }, {"parse.recovered", md2.recovered }, {"parse.dropped", md2.dropped }, {"parse.pollChecks", md2.pollChecks }, {"parse.enqueued", md2.enqueued }, {"parse.resolvedViaStatus", md2.resolvedViaStatus }, {"parse.resolvedViaDetails", md2.resolvedViaDetails }
    });

        // Per-player pending summary (stats view) at end of full run
        foreach (var pid in players)
        {
            var pendingList = MatchProcessor.GetPendingList(pid);
            var attempts = MatchProcessor.GetAttemptsSnapshot(pid);
            var meta = MatchProcessor.GetPendingMetaSnapshot(pid);
            var sample = pendingList.Take(10).ToList();
            var maxAttempts = pendingList.Count == 0 ? 0 : pendingList.Max(id => attempts.TryGetValue(id, out var n) ? n : 0);
            var oldestTs = pendingList.Count == 0 ? (long?)null : pendingList.Select(id => meta.TryGetValue(id, out var ts) ? ts : 0L).DefaultIfEmpty(0).Min();
            Logger.Info("pending summary", ctx: new Dictionary<string, object?> {
                {"player", pid },
                {"count", pendingList.Count },
                {"maxAttempts", maxAttempts },
                {"oldestLastChecked", oldestTs },
                {"sampleMatchIds", sample }
            });
        }

        // Stop background parse workers gracefully
        if (heartbeatCts != null) { try { heartbeatCts.Cancel(); } catch { } }
        if (heartbeatTask != null) { try { await heartbeatTask; } catch { } }
        MatchProcessor.StopParseBackground();
    }

    private static async Task HeartbeatLoop(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (queued, workers) = MatchProcessor.ParseQueueSnapshot();
                var playersProgress = ProgressTracker.Snapshot();
                // Keep payload small in console: show first 10
                var top = playersProgress
                    .OrderBy(p => p.PlayerId)
                    .Take(10)
                    .Select(p => new {
                        p.PlayerId, p.Processed, p.Total, p.Enqueued, p.ResolvedViaDetails, p.ResolvedViaStatus, p.Dropped, p.PendingRemaining
                    })
                    .ToList();
                Logger.Info("heartbeat", ctx: new Dictionary<string, object?> {
                    {"queue.queued", queued }, {"queue.workers", workers }, {"playersShown", top.Count }, {"playersTotal", playersProgress.Count }, {"players", top }
                });
                await Task.Delay(interval, ct);
            }
            catch (TaskCanceledException) { break; }
            catch (Exception ex)
            {
                Logger.Warn($"heartbeat error: {ex.Message}");
                try { await Task.Delay(interval, ct); } catch { break; }
            }
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 100,
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        var http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Dota2RampageTracker/1.0 (+https://github.com/Zeroks77/Dota2RampageTracker)");
        return http;
    }

    private static string ResolveOutputDirectory()
    {
        // Walk up to find the repo root (directory containing .git). If not found, fall back to CWD.
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return Path.Combine(dir.FullName, "Players");
            }
            dir = dir.Parent;
        }
        return Path.Combine(Directory.GetCurrentDirectory(), "Players");
    }

   static void LoadPlayerDirectoryMapping()
{
    if (File.Exists(mappingFilePath))
    {
        var jsonData = File.ReadAllText(mappingFilePath);
    playerDirectoryMapping = JsonConvert.DeserializeObject<Dictionary<long, string>>(jsonData) ?? new Dictionary<long, string>();
    }
}

static void SavePlayerDirectoryMapping()
{
    var jsonData = JsonConvert.SerializeObject(playerDirectoryMapping, Formatting.Indented);
    File.WriteAllText(mappingFilePath, jsonData);
}

    static async Task<bool> IsApiAlive()
    {
        string url = OpenDotaRampage.Helpers.ApiHelper.AppendApiKey("https://api.opendota.com/api/health");
        try
        {
            var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}