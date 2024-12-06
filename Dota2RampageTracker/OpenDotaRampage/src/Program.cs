using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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

        // Check if the configuration file exists
        if (!File.Exists("appsettings.json"))
        {
            OpenDotaRampage.Helpers.ConfigurationManager.CreateConfigurationFile();
        }

        // Check if the GitHub configuration file exists
        if (!File.Exists("githubsettings.json"))
        {
            OpenDotaRampage.Helpers.ConfigurationManager.CreateGitHubConfigurationFile();
        }

        // Load configuration
        var config = OpenDotaRampage.Helpers.ConfigurationManager.LoadConfiguration();
        apiKey = config["ApiKey"];
        players = config.GetSection("Players").Get<Dictionary<string, long>>();

        // Fetch hero data
        heroData = await HeroDataFetcher.FetchHeroData(client);

        // Replace with the player's name you want to check
        Console.WriteLine("Enter the player's name to check for rampages:");
        string playerName = Console.ReadLine();
        if (players.TryGetValue(playerName, out long playerId))
        {
            // Fetch player's Steam name
            string steamName = await HeroDataFetcher.GetPlayerSteamName(client, playerId);

            Console.WriteLine("Do you want to check a specific match? (yes/no):");
            string checkSpecificMatch = Console.ReadLine().ToLower();

            if (checkSpecificMatch == "yes")
            {
                Console.WriteLine("Enter the match ID to check:");
                if (long.TryParse(Console.ReadLine(), out long matchId))
                {
                    await MatchProcessor.CheckSpecificMatch(client, playerId, matchId);
                }
                else
                {
                    Console.WriteLine("Invalid match ID.");
                }
            }
            else
            {
                var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName);

                foreach (var match in rampageMatches)
                {
                    Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
                }

                MarkdownGenerator.GenerateMarkdown(steamName, rampageMatches, heroData, (int)playerId);

                // Commit and push the markdown file to the current Git repository
                GitHelper.CommitAndPush(Directory.GetCurrentDirectory(), Path.Combine(outputDirectory, steamName), Path.Combine(outputDirectory, steamName, "Rampages.md"), steamName);
            }
        }
        else
        {
            Console.WriteLine($"Player '{playerName}' not found in configuration.");
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