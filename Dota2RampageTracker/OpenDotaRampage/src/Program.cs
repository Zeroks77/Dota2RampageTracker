using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
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
    private static Dictionary<string, string> steamNames = new Dictionary<string, string>();

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

        // Handle CLI options
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "request-parsing":
                    await RequestParsingForFixedMatches();
                    return;
                case "store-match-ids":
                    StoreMatchIdsLocally(args.Skip(1).ToArray());
                    return;
                default:
                    Console.WriteLine("Invalid option. Use 'request-parsing' or 'store-match-ids'.");
                    return;
            }
        }

        // Generate markdown files for all players
        foreach (var player in players)
        {
            string playerName = player.Key;
            long playerId = player.Value;

            // Fetch player's Steam name
            string steamName = await HeroDataFetcher.GetPlayerSteamName(client, playerId);
            steamNames[playerName] = steamName;

            var rampageMatches = await MatchProcessor.GetRampageMatches(client, playerId, steamName);

            foreach (var match in rampageMatches)
            {
                Console.WriteLine($"Match ID: {match.MatchId}, Rampages: {match.Players[0].MultiKills}");
            }

            MarkdownGenerator.GenerateMarkdown(steamName, rampageMatches, heroData, (int)playerId);
        }

        // Generate the main README file with quick links to all players' markdown rampage files
        MarkdownGenerator.GenerateMainReadme(steamNames);

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

    static async Task RequestParsingForFixedMatches()
    {
        var matchIds = LoadMatchIdsFromJson("match_ids.json");

        foreach (var matchId in matchIds)
        {
            await MatchProcessor.RequestMatchParsing(client, matchId);
            Console.WriteLine($"Requested parsing for match ID: {matchId}");
        }

        // Store only the match IDs back into the file
        StoreMatchIdsLocally(matchIds.Select(id => id.ToString()).ToArray());
    }

    static void StoreMatchIdsLocally(string[] matchIds)
    {
        string filePath = Path.Combine(outputDirectory, "match_ids.txt");
        var distinctMatchIds = new HashSet<string>(matchIds);

        if (File.Exists(filePath))
        {
            var existingMatchIds = File.ReadAllLines(filePath);
            distinctMatchIds.UnionWith(existingMatchIds);
        }

        File.WriteAllLines(filePath, distinctMatchIds);
        Console.WriteLine("Match IDs stored locally.");
    }

    static List<long> LoadMatchIdsFromJson(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine($"File not found: {filePath}");
            return new List<long>();
        }

        var jsonData = File.ReadAllText(filePath);
        var matches = JsonConvert.DeserializeObject<List<Match>>(jsonData);
        return matches.Select(m => m.MatchId).Distinct().ToList();
    }
}