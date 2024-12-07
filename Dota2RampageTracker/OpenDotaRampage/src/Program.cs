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
        foreach (var playerId in players)
        {
            long lastCheckedMatchId = MatchProcessor.ReadLastCheckedMatchId(playerId.ToString());
            var matches = await MatchProcessor.GetPlayerMatches(client, playerId, lastCheckedMatchId);
            totalNewMatches += matches.Count();
        }

        // Set the rate limit based on the total number of new matches
        if (totalNewMatches < 2000)
        {
            RateLimiter.SetRateLimit(60, false); // 60 calls per minute without API key
        }
        else
        {
            RateLimiter.SetRateLimit(1100, true); // Use API key and existing rate limiting logic
        }

        // Process each player
        foreach (var playerId in players)
        {
            // Fetch player's Steam name
            string steamName = await HeroDataFetcher.GetPlayerSteamName(client, playerId);

            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName);

            foreach (var match in rampageMatches)
            {
                Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, rampageMatches, heroData, (int)playerId);
        }

        // Stop the stopwatch
        stopwatch.Stop();
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