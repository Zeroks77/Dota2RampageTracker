using System.IO;
using Newtonsoft.Json;
using OpenDotaRampage.Models;
using Xunit;

public class MatchDeserializationTests
{
    [Fact]
    public void TestDeserializeMatch()
    {
        // Arrange
        string jsonFilePath = "C:\\Users\\Dominik\\Desktop\\Rampage Tracker\\OpenDotaRampage.Tests\\TestDataSet.json";
        string jsonData = File.ReadAllText(jsonFilePath);

        // Act
        Match match = JsonConvert.DeserializeObject<Match>(jsonData);

        // Assert
        Assert.NotNull(match);
        Assert.Equal(6493708345, match.MatchId);
        Assert.NotNull(match.Players);

        var player = match.Players.Single(p => p.AccountId == 308948139);
        Assert.Equal(6493708345, player.MatchId);
        Assert.Equal(308948139, player.AccountId);
        Assert.Equal(59, player.HeroId);
        Assert.NotNull(player.MultiKills);
        Assert.Equal(2, player.MultiKills[2]);
        Assert.Equal(1, player.MultiKills[3]);
    }
}