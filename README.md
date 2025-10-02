# Dota 2 Rampage Tracker
This repository contains rampage tracking data for various Dota 2 players.

> Last updated: 2025-10-02 19:35 UTC

## Players
| Player Name | Profile Picture | Rampages | Rampage Rate | Win Rate (Total) | Win Rate (Unranked) | Win Rate (Ranked) | Rampage File |
|-------------|-----------------|----------|--------------|------------------|---------------------|-------------------|--------------|
| Gary the Carry | ![Profile Picture](https://avatars.steamstatic.com/23f8ee4662d83a5959ef06b8cf948d66955997cc_full.jpg) | 24 | 0.80% | 59.31% | 62.47% | 58.97% | [Rampages](./Players/169325410/Rampages.md) |

## Configuration (OpenDota API and Rate Limiting)
- Set Program.apiKey to your OpenDota API key and enable RateLimiter.SetRateLimit(true) at startup.
- Ensure every OpenDota request appends ?api_key=YOUR_KEY when enabled.
- Keep concurrency modest: RateLimiter.concurrencyLimiter = new SemaphoreSlim(4, 4) is safer when hitting multiple endpoints.

## Troubleshooting: 429 Too Many Requests
If you see HttpRequestException 429:
- Do not call EnsureSuccessStatusCode() before checking for 429.
- Respect Retry-After header when present; otherwise back off with exponential delay + jitter.
- Centralize all HTTP calls through a helper (e.g., ApiHelper.GetStringWithBackoff) and make every OpenDota call go through it.
- Example retry approach you can apply in ApiHelper:
  ```csharp
  // Pseudocode pattern to apply inside ApiHelper.GetStringWithBackoff
  for (var attempt = 0; attempt < maxRetries; attempt++) {
      var resp = await client.SendAsync(req, ct);
      if (resp.StatusCode == (HttpStatusCode)429) {
          var retryAfter = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(90, Math.Pow(2, attempt) * 2));
          await Task.Delay(retryAfter + TimeSpan.FromMilliseconds(Random.Shared.Next(250, 750)), ct);
          continue;
      }
      if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsStringAsync(ct);
      // transient 5xx -> backoff similarly, else break/throw
  }
  throw new HttpRequestException("Exceeded retries");
  ```
- Also apply the same backoff for HeroDataFetcher.GetGameModes and any other OpenDota endpoints.
- Combine with RateLimiter.EnsureRateLimit() before each call.
