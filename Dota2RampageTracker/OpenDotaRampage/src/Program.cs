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
    private static List<long> playerIds;
    private static Dictionary<int, Hero> heroData;

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
        Console.WriteLine($"API Key: {apiKey}");
        Console.WriteLine($"Players CSV: {playersCsv}");

        playerIds = playersCsv.Split(',').Select(long.Parse).ToList();
        Console.WriteLine("Player IDs:");
        foreach (var playerId in playerIds)
        {
            Console.WriteLine($"  {playerId}");
        }

        // Fetch hero data
        heroData = await HeroDataFetcher.FetchHeroData(client);
        Console.WriteLine("Hero data fetched successfully.");

        // Process each player ID
        foreach (var playerId in playerIds)
        {
            // Fetch player's Steam name
            string steamName = await HeroDataFetcher.GetPlayerSteamName(client, playerId);
            Console.WriteLine($"Player ID: {playerId}, Steam Name: {steamName}");

            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName);

            foreach (var match in rampageMatches)
            {
                Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, rampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file with quick links to all players' markdown rampage files
        MarkdownGenerator.GenerateMainReadme(playerIds.ToDictionary(id => id.ToString(), id => id.ToString()));

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