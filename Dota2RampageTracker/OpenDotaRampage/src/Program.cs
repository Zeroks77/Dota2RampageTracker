using System.Diagnostics;
using System.Net;
using dotenv.net;
using Newtonsoft.Json;
using OpenDotaRampage.Helpers;
using OpenDotaRampage.Models;

class Program
{
    public static readonly string outputDirectory = "Players";
    public static readonly HttpClient client = new HttpClient();
    public static string apiKey;
    public static readonly Stopwatch stopwatch = new Stopwatch();
    private static List<long> players;
    private static Dictionary<int, Hero> heroData;
   private static Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)> steamProfiles = new Dictionary<string, (string SteamName, string AvatarUrl, Dictionary<string, int> Totals, CountsResponse Counts)>();
     private static Dictionary<long, string> playerDirectoryMapping = new Dictionary<long, string>();
    private static readonly string mappingFilePath = "player_directory_mapping.json";

    static async Task Main(string[] args)
    {
        // Load environment variables from .env file
        DotEnv.Load();

        // Initialize the rate limit reset timer
        RateLimiter.Initialize();

        // Start the stopwatch to track total time spent
        stopwatch.Start();

        // Check if the API is alive
        if (!await IsApiAlive())
        {
            Console.WriteLine("The OpenDota API is currently unavailable. Please try again later.");
            return;
        }

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
        players = playersCsv.Split(',').Select(long.Parse).ToList();

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

            string encodedPlayerName = WebUtility.UrlEncode(steamName);
            // Check if the directory name has changed
            if (playerDirectoryMapping.TryGetValue(playerId, out var oldDirectoryName))
            {
                // Check if the directory name has changed
            string oldDirectoryPath = Path.Combine(outputDirectory, oldDirectoryName.ToString());
            string newDirectoryPath = Path.Combine(outputDirectory, encodedPlayerName);
                if (Directory.Exists(oldDirectoryPath) && oldDirectoryPath != newDirectoryPath)
                {
                    Directory.Move(oldDirectoryPath, newDirectoryPath);
                    playerDirectoryMapping[playerId] = encodedPlayerName;
                }
            }
            else
            {
                playerDirectoryMapping[playerId] = steamName;
            }

            long lastCheckedMatchId = MatchProcessor.ReadLastCheckedMatchId(encodedPlayerName);
            var matches = await MatchProcessor.GetPlayerMatches(client, playerId, lastCheckedMatchId);
            int playerMatchesCount = matches.Count();
            totalNewMatches += playerMatchesCount;
            playerMatches[playerId] = matches.ToList();
            Console.WriteLine($"Player ID: {playerId}, Steam Name: {steamName}, New Matches: {playerMatchesCount}");
        }

        // Save updated player directory mapping to JSON file
        SavePlayerDirectoryMapping();

        Console.WriteLine($"Total New Matches: {totalNewMatches}");

        // Set the rate limit based on the total number of new matches
        if (totalNewMatches < 2000)
        {
            RateLimiter.SetRateLimit(false); // 60 calls per minute without API key
        }
        else
        {
            RateLimiter.SetRateLimit(true); // Use API key and existing rate limiting logic
        }

        // Process each player
        foreach (var playerId in players)
        {
            // Fetch player's Steam name, avatar URL, and totals data
            var (steamName, avatarUrl, totals, counts) = steamProfiles[playerId.ToString()];

            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName, playerMatches[playerId]);
            // Load cached rampage matches
            var cachedRampageMatches = MarkdownGenerator.LoadRampageMatchesFromCache(steamName);

            // Combine new and cached rampage matches, ensuring distinct matches
            var allRampageMatches = rampageMatches.Concat(cachedRampageMatches)
                .GroupBy(m => m.MatchId)
                .Select(g => g.First())
                .Distinct()
                .ToList();

            steamProfiles[playerId.ToString()].Totals["rampages"] = allRampageMatches.Count;
            foreach (var match in rampageMatches)
            {
                Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, allRampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file
        MarkdownGenerator.GenerateMainReadme(steamProfiles, gameModes, lobbyTypes, patches);

        // Stop the stopwatch
        stopwatch.Stop();
        Console.WriteLine($"Total time spent: {stopwatch.Elapsed:hh\\:mm\\:ss}");
    }

    static void LoadPlayerDirectoryMapping()
    {
        if (File.Exists(mappingFilePath))
        {
            var jsonData = File.ReadAllText(mappingFilePath);
            playerDirectoryMapping = JsonConvert.DeserializeObject<Dictionary<long, string>>(jsonData);
        }
    }

    static void SavePlayerDirectoryMapping()
    {
        var jsonData = JsonConvert.SerializeObject(playerDirectoryMapping, Formatting.Indented);
        File.WriteAllText(mappingFilePath, jsonData);
    }

    static async Task<bool> IsApiAlive()
    {
        string url = "https://api.opendota.com/api/health";
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