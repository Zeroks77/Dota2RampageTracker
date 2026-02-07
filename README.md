# Dota 2 Rampage Tracker
Last updated: 2026-02-06 12:46 UTC

> Note: All game data is sourced via the OpenDota API. This project is not affiliated with Valve or OpenDota.
> Data source: OpenDota (https://www.opendota.com)

## Players

| Player | Profile | Rampages/Matches | Winrate | Radiant | Dire | Rampages |
|:-------|:-------:|------------------:|--------:|--------:|-----:|:---------|
| [Lucky](Players/308948139/README.md) | <img src="https://avatars.steamstatic.com/1191c81a57194f64acfcda94f0fd0cb94e92eff7_full.jpg" width="32" height="32"/> | 28/5599 | 54,51% | 55,13% | 53,89% | [View](Players/308948139/Rampages.md) |
| [Gary the Carry](Players/169325410/README.md) | <img src="https://avatars.steamstatic.com/23f8ee4662d83a5959ef06b8cf948d66955997cc_full.jpg" width="32" height="32"/> | 24/3101 | 60,24% | 62,28% | 58,15% | [View](Players/169325410/Rampages.md) |
| [Xenas23](Players/181342370/README.md) | <img src="https://avatars.steamstatic.com/16392e7c2bf30770c48c4b989eef4a19f237d548_full.jpg" width="32" height="32"/> | 13/5927 | 55,26% | 56,51% | 53,97% | [View](Players/181342370/Rampages.md) |
| [Zero](Players/183063377/README.md) | <img src="https://avatars.steamstatic.com/c0a975434fc5b15f662cbe8214fc898c493b55ea_full.jpg" width="32" height="32"/> | 8/8087 | 50,85% | 52,27% | 49,37% | [View](Players/183063377/Rampages.md) |
| [Mupfel](Players/131232145/README.md) | <img src="https://avatars.steamstatic.com/5975408a7d136abfeb6160943f0db7743d542d54_full.jpg" width="32" height="32"/> | 5/3991 | 55,02% | 55,04% | 55,01% | [View](Players/131232145/Rampages.md) |
| [Kret](Players/226354794/README.md) | <img src="https://avatars.steamstatic.com/c0710d11651022f0fbcd99159677a7acfc6e6a18_full.jpg" width="32" height="32"/> | 3/935 | 58,50% | 63,05% | 53,73% | [View](Players/226354794/Rampages.md) |
| [jarjeer](Players/1002536896/README.md) | <img src="https://avatars.steamstatic.com/2c33a4d4725158a4546f3414b2a76891d0abb218_full.jpg" width="32" height="32"/> | 2/1399 | 49,89% | 52,76% | 47,12% | [View](Players/1002536896/Rampages.md) |
| [Virvacus](Players/1127238076/README.md) | <img src="https://avatars.steamstatic.com/45f83173783fdfe00f08ac4d7872856a2d82677e_full.jpg" width="32" height="32"/> | 0/2877 | 50,09% | 53,46% | 46,82% | [View](Players/1127238076/Rampages.md) |

## How it works

- For each player, the tool fetches recent match summaries from OpenDota (no replays).
- For new/unparsed matches, it requests a parse job and later evaluates the full match payload.
- A centralized parse queue avoids duplicate requests for matches shared by multiple tracked players.
- Rampages are detected when multi_kills[5] > 0 for the player in match data.
- Results are stored per player under `data/<playerId>/` (Rampages.json, Matches.json, profile.json, lastchecked.txt).
- The README files are generated from these local files (no parsing required). 

### Modes
- `new`: checks new matches per player, requests parsing if necessary, writes rampages, updates READMEs.
- `parse`: drains the global parse queue, writes found rampages, updates READMEs.
- `full`: clears local data and runs like `new`.
- `regen-readme`: generates only the READMEs from local files (no API/parsing).
