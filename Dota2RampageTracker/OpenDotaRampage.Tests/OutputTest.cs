using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using OpenDotaRampage.Models;
using Xunit;

public class MarkdownGeneratorTests
{
    [Fact]
    public void TestGenerateMarkdown()
    {
        // Arrange
        var rampageMatches = new List<Match>
        {
            new Match
            {
                MatchId = 1234567890,
                Players = new List<Player>
                {
                    new Player
                    {
                        AccountId = 123,
                        HeroId = 1,
                        MultiKills = new Dictionary<int, int> { { 5, 1 } }
                    }
                }
            },
            new Match
            {
                MatchId = 1234567891,
                Players = new List<Player>
                {
                    new Player
                    {
                        AccountId = 123,
                        HeroId = 2,
                        MultiKills = new Dictionary<int, int> { { 5, 1 } }
                    }
                }
            }
        };

        var heroNames = new Dictionary<int, string>
        {
            { 1, "Anti-Mage" },
            { 2, "Axe" }
        };

        var steamName = "TestPlayer";
        var outputDirectory = "TestOutput";
        var filePath = Path.Combine(outputDirectory, steamName, "Rampages.md");

        // Act
        GenerateMarkdown(steamName, rampageMatches, heroNames, outputDirectory);

        // Assert
        Assert.True(File.Exists(filePath), "The markdown file was not created.");

        var expectedContent = new List<string>
        {
            $"Player {steamName} has got 2 total Rampages",
            "### Anti-Mage",
            "| Match ID |",
            "|----------|",
            "| [Match URL](https://www.opendota.com/matches/1234567890) |",
            "### Axe",
            "| Match ID |",
            "|----------|",
            "| [Match URL](https://www.opendota.com/matches/1234567891) |"
        };

        var actualContent = File.ReadAllLines(filePath);
        foreach (var expectedLine in expectedContent)
        {
            Assert.Contains(expectedLine, actualContent);
        }

        // Clean up
        if (Directory.Exists(outputDirectory))
        {
            Directory.Delete(outputDirectory, true);
        }
    }

    private void GenerateMarkdown(string steamName, List<Match> rampageMatches, Dictionary<int, string> heroNames, string outputDirectory)
    {
        string playerDirectory = Path.Combine(outputDirectory, steamName);
        Directory.CreateDirectory(playerDirectory);
        string filePath = Path.Combine(playerDirectory, "Rampages.md");

        using (var writer = new StreamWriter(filePath))
        {
            writer.WriteLine($"Player {steamName} has got {rampageMatches.Count} total Rampages\n");

            var groupedRampages = rampageMatches
                .SelectMany(match => match.Players
                    .Where(player => player.HeroId.HasValue)
                    .Select(player => new { match.MatchId, player.HeroId }))
                .GroupBy(x => x.HeroId)
                .ToDictionary(g => g.Key, g => g.Select(x => x.MatchId).ToList());

            foreach (var heroId in groupedRampages.Keys)
            {
                string heroName = heroNames.ContainsKey(heroId.Value) ? heroNames[heroId.Value] : heroId.ToString();
                writer.WriteLine($"### {heroName}");
                writer.WriteLine("| Match ID |");
                writer.WriteLine("|----------|");

                foreach (var matchId in groupedRampages[heroId])
                {
                    writer.WriteLine($"| [Match URL](https://www.opendota.com/matches/{matchId}) |");
                }

                writer.WriteLine();
            }
        }
    }
}