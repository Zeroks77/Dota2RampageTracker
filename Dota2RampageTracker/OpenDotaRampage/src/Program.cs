using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Newtonsoft.Json;
using OpenDotaRampage.Helpers;
using OpenDotaRampage.Models;

class Program
{
    public static readonly string outputDirectory = "Players";
    public static readonly HttpClient client = new HttpClient();
    public static string apiKey;
    public static readonly Stopwatch stopwatch = new Stopwatch();
    private static Dictionary<string, long> players;
    private static Dictionary<int, Hero> heroData;

    static async Task Main(string[] args)
    {
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
        apiKey = Environment.GetEnvironmentVariable("ApiKey");
        var playersJson = Environment.GetEnvironmentVariable("Players");
        players = JsonConvert.DeserializeObject<Dictionary<string, long>>(playersJson);

        // Fetch hero data
        heroData = await HeroDataFetcher.FetchHeroData(client);

        // Replace with the player's name you want to check
        foreach (var player in players)
        {
            string playerName = player.Key;
            long playerId = player.Value;

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