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

    static async Task Main(string[] args)
    {
        // Load environment variables from .env file
        DotEnv.Load();

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

        // Check if the required environment variables exist
        if (string.IsNullOrEmpty(playersCsv))
        {
            Console.WriteLine("Error: PLAYERS environment variable is missing.");
            return;
        }

    // Parse the players CSV into a list of long
    players = playersCsv.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(long.Parse).ToList();
    Console.WriteLine($"Loaded PLAYERS: {string.Join(",", players)}");

        // Load player directory mapping from JSON file
        LoadPlayerDirectoryMapping();

        // Fetch hero data
        heroData = await HeroDataFetcher.FetchHeroData(client);

        // Fetch game modes, lobby types, and patches
    var gameModes = await HeroDataFetcher.GetGameModes(client);
    var lobbyTypes = await HeroDataFetcher.GetLobbyTypes(client);
    var patches = await HeroDataFetcher.GetPatches(client);

        // Determine the total number of new matches for all players
        int totalNewMatches = 0;
        var playerMatches = new Dictionary<long, List<Match>>();
        foreach (var playerId in players)
        {
            // Fetch player's Steam name, avatar URL, and totals data
             var (steamName, avatarUrl) = await HeroDataFetcher.GetPlayerSteamName(client, playerId);
            var totals = await HeroDataFetcher.GetPlayerTotals(client, playerId);
            var counts = await HeroDataFetcher.GetPlayerCounts(client, playerId);
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

            long lastCheckedMatchId = MatchProcessor.ReadLastCheckedMatchId(playerId.ToString());
            var matches = await MatchProcessor.GetPlayerMatches(client, playerId, lastCheckedMatchId);
            int playerMatchesCount = matches.Count();
            totalNewMatches += playerMatchesCount;
            playerMatches[playerId] = matches.ToList();
            Console.WriteLine($"Player ID: {playerId}, Steam Name: {steamName}, New Matches: {playerMatchesCount}");
        }

        // Save updated player directory mapping to JSON file
        SavePlayerDirectoryMapping();

        Console.WriteLine($"Total New Matches: {totalNewMatches}");

        // Set the rate limit based on API key availability
        RateLimiter.SetRateLimit(!string.IsNullOrWhiteSpace(apiKey)); // 1000/min with API key, 60/min otherwise

        // Process each player
        foreach (var playerId in players)
        {
            // Fetch player's Steam name, avatar URL, and totals data
            var (steamName, avatarUrl, totals, counts) = steamProfiles[playerId.ToString()];
            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, playerMatches[playerId]);
            // Load cached rampage matches
            var cachedRampageMatches = MarkdownGenerator.LoadRampageMatchesFromCache(playerId.ToString());

            // Combine new and cached rampage matches, ensuring distinct matches
            var allRampageMatches = rampageMatches.Concat(cachedRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .ToList();

            // Enrich missing StartTime and write back to cache so markdown can show dates
            allRampageMatches = await MatchProcessor.EnrichStartTimes(client, allRampageMatches);
            MatchProcessor.WriteRampageCache(playerId.ToString(), allRampageMatches);

            steamProfiles[playerId.ToString()].Totals["rampages"] = allRampageMatches.Count;
            foreach (var match in rampageMatches)
            {
                var firstPlayer = match.Players?.FirstOrDefault();
                var mk = firstPlayer?.MultiKills != null ? string.Join(",", firstPlayer.MultiKills.Select(kv => $"{kv.Key}:{kv.Value}")) : "-";
                Console.WriteLine($"Match ID: {match.MatchId}, MultiKills: {mk}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, allRampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file
        MarkdownGenerator.GenerateMainReadme(steamProfiles, gameModes, lobbyTypes, patches);

        // Stop the stopwatch
        stopwatch.Stop();
        Console.WriteLine($"Total time spent: {stopwatch.Elapsed:hh\\:mm\\:ss}");
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