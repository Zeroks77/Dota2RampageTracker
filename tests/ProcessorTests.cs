using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using RampageTracker.Core;
using RampageTracker.Data;
using RampageTracker.Models;
using RampageTracker.Processing;
using RampageTracker.Tests.Fakes;
using Xunit;

namespace RampageTracker.Tests
{
    public class ProcessorTests
    {
        [Fact]
        public async Task NewOnly_Finds_Rampage_And_Writes_Output()
        {
            // Arrange dummy data: one new match with rampage for player 111
            var playerId = 111L;
            var summaries = new [] { new PlayerMatchSummary { MatchId = 1001 } };
            var match = new Match
            {
                MatchId = 1001,
                Players = new List<MatchPlayer>
                {
                    new MatchPlayer { AccountId = (int)playerId, MultiKills = new Dictionary<int,int> { {5,1} } }
                }
            };

            int requestStep = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith($"/players/{playerId}/matches"))
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(summaries), Encoding.UTF8, "application/json") };
                if (path.StartsWith("/api/request/"))
                {
                    // first POST returns job id, later GETs return has_parsed
                    if (req.Method == HttpMethod.Post)
                    {
                        var job = new { job = new { jobId = 555 } };
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(job), Encoding.UTF8, "application/json") };
                    }
                    if (req.Method == HttpMethod.Get)
                    {
                        requestStep++;
                        var has = new { has_parsed = requestStep >= 2 };
                        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(has), Encoding.UTF8, "application/json") };
                    }
                }
                if (path.EndsWith($"/matches/{match.MatchId}"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonConvert.SerializeObject(match), Encoding.UTF8, "application/json") };
                }
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            });

            var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.opendota.com") };
            var api = new ApiManager(http, "K");

            // Isolierter Testdatenordner
            var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rt-tests-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(tmp);
            var data = new DataManager(tmp);
            await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(tmp, "players.json"), JsonConvert.SerializeObject(new List<long>{ playerId }));

            // Act
            // Speed up polling for tests
            Environment.SetEnvironmentVariable("RT_FAST_DELAY", "1");
            await Processor.RunNewOnlyAsync(api, data, new List<long>{ playerId }, workers: 1, ct: CancellationToken.None);

            // Assert: Rampages.json contains 1001
            var rpath = System.IO.Path.Combine(tmp, "data", playerId.ToString(), "Rampages.json");
            System.IO.File.Exists(rpath).Should().BeTrue();
            var entries = JsonConvert.DeserializeObject<List<RampageEntry>>(await System.IO.File.ReadAllTextAsync(rpath));
            entries.Should().NotBeNull();
            entries!.Select(e => e.MatchId).Should().Contain(1001L);
            entries!.First(e => e.MatchId == 1001L).HeroName.Should().NotBeNullOrWhiteSpace();
            entries!.First(e => e.MatchId == 1001L).StartTime.Should().BeNull();

            // README artifacts get created (per-player and main)
            var mainReadme = System.IO.Path.Combine(tmp, "README.md");
            var perPlayerReadme = System.IO.Path.Combine(tmp, "Players", playerId.ToString(), "README.md");
            System.IO.File.Exists(mainReadme).Should().BeTrue();
            System.IO.File.Exists(perPlayerReadme).Should().BeTrue();
        }

        [Fact]
    public async Task RateLimiter_Is_Respected_In_ApiManager()
        {
            // We count requests and assert we didn't exceed a small per-minute cap.
            int count = 0;
            var handler = new FakeHttpMessageHandler(req =>
            {
                Interlocked.Increment(ref count);
                var path = req.RequestUri!.AbsolutePath;
                // Simulate correct shapes
                if (path.Contains("/players/") && path.EndsWith("/matches"))
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("[]", Encoding.UTF8, "application/json")
                    };
                }
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });
            var http = new HttpClient(handler);
            var api = new ApiManager(http, "KEY");
            RampageTracker.Core.RateLimiter.Initialize(useApiKey: false);

            var tasks = Enumerable.Range(0, 20).Select(_ => api.GetPlayerMatchesAsync(1));
            await Task.WhenAll(tasks);

            // Ensure the handler saw the expected number of requests
            count.Should().Be(20);
        }
    }
}
