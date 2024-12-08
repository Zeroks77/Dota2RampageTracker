using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
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
    private static Dictionary<string, string> steamNames = new Dictionary<string, string>();

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

        // Fetch hero data
        heroData = await HeroDataFetcher.FetchHeroData(client);
    
        // Determine the total number of new matches for all players
        int totalNewMatches = 0;
        var playerMatches = new Dictionary<long, List<Match>>();
        foreach (var playerId in players)
        {
            // Fetch player's Steam name
            string steamName = await HeroDataFetcher.GetPlayerSteamName(client, playerId);
            steamNames[playerId.ToString()] = steamName;
            long lastCheckedMatchId = MatchProcessor.ReadLastCheckedMatchId(steamName);
            var matches = await MatchProcessor.GetPlayerMatches(client, playerId, lastCheckedMatchId);
            int playerMatchesCount = matches.Count();
            totalNewMatches += playerMatchesCount;
            playerMatches[playerId] = matches.ToList();
            Console.WriteLine($"Player ID: {playerId}, Steam Name: {steamName}, New Matches: {playerMatchesCount}");
        }


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
            // Fetch player's Steam name
            string steamName = steamNames[playerId.ToString()];

            // Check if the directory name has changed
            string oldDirectoryPath = Path.Combine(outputDirectory, playerId.ToString());
            string newDirectoryPath = Path.Combine(outputDirectory, steamName);
            if (Directory.Exists(oldDirectoryPath) && oldDirectoryPath != newDirectoryPath)
            {
                Directory.Move(oldDirectoryPath, newDirectoryPath);
            }

            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName, playerMatches[playerId]);

            foreach (var match in rampageMatches)
            {
                Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, rampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file
        MarkdownGenerator.GenerateMainReadme(steamNames);

        // Stop the stopwatch
        stopwatch.Stop();
        Console.WriteLine($"Total time spent: {stopwatch.Elapsed:hh\\:mm\\:ss}");
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