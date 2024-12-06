using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

namespace OpenDotaRampage.Helpers
{
    public static class ConfigurationManager
    {
        private static readonly string configFilePath = "appsettings.json";
        private static readonly string gitHubConfigFilePath = "githubsettings.json";

        public static void CreateConfigurationFile()
        {
            Console.WriteLine("Configuration file not found. Let's create one.");

            Console.WriteLine("Enter your OpenDota API key:");
            string apiKey = Console.ReadLine();

            var players = new Dictionary<string, long>();
            while (true)
            {
                Console.WriteLine("Enter player name (or type 'done' to finish):");
                string playerName = Console.ReadLine();
                if (playerName.ToLower() == "done")
                {
                    break;
                }

                Console.WriteLine("Enter player ID:");
                if (long.TryParse(Console.ReadLine(), out long playerId))
                {
                    players[playerName] = playerId;
                }
                else
                {
                    Console.WriteLine("Invalid player ID. Please try again.");
                }
            }

            var config = new
            {
                ApiKey = apiKey,
                Players = players
            };

            File.WriteAllText(configFilePath, JsonConvert.SerializeObject(config, Formatting.Indented));
            Console.WriteLine("Configuration file created successfully.");
        }

        public static void CreateGitHubConfigurationFile()
        {
            Console.WriteLine("GitHub configuration file not found. Let's create one.");

            Console.WriteLine("Enter your GitHub repository URL:");
            string repoUrl = Console.ReadLine();

            Console.WriteLine("Enter your GitHub username:");
            string username = Console.ReadLine();

            Console.WriteLine("Enter your GitHub email:");
            string email = Console.ReadLine();

            Console.WriteLine("Enter your GitHub personal access token:");
            string token = Console.ReadLine();

            var gitHubConfig = new
            {
                RepoUrl = repoUrl,
                Username = username,
                Email = email,
                Token = token
            };

            File.WriteAllText(gitHubConfigFilePath, JsonConvert.SerializeObject(gitHubConfig, Formatting.Indented));
            Console.WriteLine("GitHub configuration file created successfully.");
        }

        public static IConfiguration LoadConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(configFilePath, optional: false, reloadOnChange: true);
            return builder.Build();
        }

        public static IConfiguration LoadGitHubConfiguration()
        {
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile(gitHubConfigFilePath, optional: false, reloadOnChange: true);
            return builder.Build();
        }
    }
}