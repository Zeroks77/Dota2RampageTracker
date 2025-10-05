using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using Newtonsoft.Json;
using RampageTracker.Core;
using RampageTracker.Models;
using RampageTracker.Tests.Fakes;
using Xunit;

namespace RampageTracker.Tests
{
    public class ApiTests
    {
        [Fact]
        public async Task WithApiKey_Appends_Query_Param()
        {
            var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                // Player matches endpoint returns an array
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
            var http = new HttpClient(handler);
            var api = new ApiManager(http, "KEY123");

            await api.GetPlayerMatchesAsync(169325410);

            handler.Requests.Should().ContainSingle();
            var url = handler.Requests[0].RequestUri!.ToString();
            url.Should().Contain("api_key=KEY123");
        }

        [Fact]
        public async Task GetMatch_Parses_MultiKills()
        {
            var match = new Match
            {
                MatchId = 123,
                Players = new System.Collections.Generic.List<MatchPlayer>
                {
                    new MatchPlayer { AccountId = 169325410, MultiKills = new System.Collections.Generic.Dictionary<int,int> { {5,1} } }
                }
            };
            var json = JsonConvert.SerializeObject(match);
            var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
            var http = new HttpClient(handler);
            var api = new ApiManager(http, "KEY");

            var got = await api.GetMatchAsync(123);
            got!.Players!.First().MultiKills!.Should().ContainKey(5);
        }

        [Fact]
        public async Task GetPlayerProfile_Parses_Profile_Object()
        {
            var payload = new PlayerProfileResponse
            {
                Profile = new PlayerProfile { AccountId = 42, PersonaName = "Zero", AvatarFull = "http://avatar" }
            };
            var json = JsonConvert.SerializeObject(payload);
            var handler = new FakeHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
            var http = new HttpClient(handler);
            var api = new ApiManager(http, null);

            var prof = await api.GetPlayerProfileAsync(42);
            prof.Should().NotBeNull();
            prof!.PersonaName.Should().Be("Zero");
            prof.AvatarFull.Should().Be("http://avatar");
        }
    }
}
