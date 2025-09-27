using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace OpenDotaRampage.Helpers
{
    public static class ApiHelper
    {
        public static string AppendApiKey(string url)
        {
            var key = Program.apiKey;
            if (string.IsNullOrWhiteSpace(key))
            {
                return url;
            }
            var separator = url.Contains("?") ? "&" : "?";
            return url + separator + "api_key=" + Uri.EscapeDataString(key);
        }

        public static async Task<string> GetStringWith429Retry(HttpClient client, string url, int maxRetries = 3)
        {
            url = AppendApiKey(url);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var resp = await client.GetAsync(url);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(3000);
                    continue;
                }
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsStringAsync();
            }
            // Last attempt
            var last = await client.GetAsync(url);
            last.EnsureSuccessStatusCode();
            return await last.Content.ReadAsStringAsync();
        }

        public static async Task<HttpResponseMessage> GetAsyncWith429Retry(HttpClient client, string url, int maxRetries = 3)
        {
            url = AppendApiKey(url);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var resp = await client.GetAsync(url);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(3000);
                    continue;
                }
                return resp;
            }
            return await client.GetAsync(url);
        }

        public static async Task<HttpResponseMessage> PostWith429Retry(HttpClient client, string url, HttpContent? content = null, int maxRetries = 3)
        {
            url = AppendApiKey(url);
            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                var resp = await client.PostAsync(url, content);
                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await Task.Delay(3000);
                    continue;
                }
                return resp;
            }
            return await client.PostAsync(url, content);
        }
    }
}
