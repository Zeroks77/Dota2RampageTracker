using System.Collections.Generic;
using System.Threading.Tasks;
using Moq;
using Newtonsoft.Json;
using OpenDotaRampage.Models;
using Xunit;

public class RampageFinderTests
{
    [Fact]
    public async Task TestGetRampageMatches()
    {
        // Arrange
        var mockHttpClient = new Mock<HttpClient>();
        var playerId = 183063377;
        var matchIds = new List<long> { 6696217177, 5410339630, 5207599129 };
        var expectedRampageMatches = new List<OpenDotaRampage.Models.Match>
        {
            new OpenDotaRampage.Models.Match
            {
                MatchId = 6493708345,
                Players = new List<Player>
                {
                    new Player
                    {
                        MatchId = 6493708345,
                        AccountId = playerId,
                        HeroId = 59,
                        MultiKills = new Dictionary<int, int> { { 2, 2 }, { 3, 1 } }
                    }
                }
            }
        };

        var mockResponse = new HttpResponseMessage
        {
            StatusCode = System.Net.HttpStatusCode.OK,
            Content = new StringContent(JsonConvert.SerializeObject(expectedRampageMatches))
        };

        mockHttpClient.Setup(client => client.GetAsync(It.IsAny<string>())).ReturnsAsync(mockResponse);

        var rampageFinder = new RampageFinder(mockHttpClient.Object);

        // Act
        var rampageMatches = await rampageFinder.GetRampageMatches(playerId, matchIds);

        // Assert
        Assert.NotNull(rampageMatches);
        Assert.Single(rampageMatches);
        Assert.Equal(expectedRampageMatches[0].MatchId, rampageMatches[0].MatchId);
        Assert.Equal(expectedRampageMatches[0].Players[0].AccountId, rampageMatches[0].Players[0].AccountId);
        Assert.Equal(expectedRampageMatches[0].Players[0].HeroId, rampageMatches[0].Players[0].HeroId);
        Assert.Equal(expectedRampageMatches[0].Players[0].MultiKills, rampageMatches[0].Players[0].MultiKills);
    }
}

public class RampageFinder
{
    private readonly HttpClient _httpClient;

    public RampageFinder(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<List<OpenDotaRampage.Models.Match>> GetRampageMatches(long playerId, List<long> matchIds)
    {
        var rampageMatches = new List<OpenDotaRampage.Models.Match>();

        foreach (var matchId in matchIds)
        {
            var response = await _httpClient.GetAsync($"https://api.opendota.com/api/matches/{matchId}");
            if (response.IsSuccessStatusCode)
            {
                var match = JsonConvert.DeserializeObject<OpenDotaRampage.Models.Match>(await response.Content.ReadAsStringAsync());
                if (match != null)
                {
                    foreach (var player in match.Players)
                    {
                        if (player.AccountId == playerId && player.MultiKills != null && player.MultiKills.ContainsKey(5))
                        {
                            rampageMatches.Add(match);
                            break;
                        }
                    }
                }
            }
        }

        return rampageMatches;
    }
}