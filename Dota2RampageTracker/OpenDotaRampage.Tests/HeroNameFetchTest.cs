using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Xunit;

public class HeroMappingTests
{
    [Fact]
    public async Task TestFetchHeroData()
    {
        // Arrange
        var httpClient = new HttpClient();

        // Act
        var heroData = await FetchHeroData(httpClient);

        // Assert
        Assert.NotEmpty(heroData);
        Assert.True(heroData.ContainsKey(1)); // Example: Check if Anti-Mage (ID 1) is present
        Assert.Equal("Anti-Mage", heroData[1]); // Example: Check if ID 1 maps to "Anti-Mage"
    }

    private async Task<Dictionary<int, string>> FetchHeroData(HttpClient client)
    {
        var heroData = new Dictionary<int, string>();
        string url = "https://liquipedia.net/dota2/Hero_ID";
        var response = await client.GetStringAsync(url);

        var htmlDoc = new HtmlDocument();
        htmlDoc.LoadHtml(response);

        var rows = htmlDoc.DocumentNode.SelectNodes("//table[contains(@class, 'wikitable')]//tr");
        if (rows != null)
        {
            foreach (var row in rows.Skip(1)) // Skip header row
            {
                var cells = row.SelectNodes("td");
                if (cells != null && cells.Count >= 2)
                {
                    if (int.TryParse(cells[1].InnerText.Trim(), out int heroId))
                    {
                        string heroName = cells[0].InnerText.Trim();
                        heroData[heroId] = heroName;
                    }
                }
            }
        }
        else
        {
            Console.WriteLine("No hero data found on the page.");
        }

        return heroData;
    }
}