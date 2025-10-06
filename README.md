# Dota 2 Rampage Tracker
Last updated: 2025-10-06 16:04 UTC

> Note: All game data is sourced via the OpenDota API. This project is not affiliated with Valve or OpenDota.
> Data source: OpenDota (https://www.opendota.com)

## Players

| Player | Profile | Rampages/Matches | Winrate | Radiant | Dire | Rampages |
|:-------|:-------:|------------------:|--------:|--------:|-----:|:---------|
| [1002536896](Players/1002536896/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/1002536896/Rampages.md) |
| [1127238076](Players/1127238076/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/1127238076/Rampages.md) |
| [131232145](Players/131232145/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/131232145/Rampages.md) |
| [169325410](Players/169325410/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/169325410/Rampages.md) |
| [181342370](Players/181342370/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/181342370/Rampages.md) |
| [183063377](Players/183063377/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/183063377/Rampages.md) |
| [188889560](Players/188889560/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/188889560/Rampages.md) |
| [226354794](Players/226354794/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/226354794/Rampages.md) |
| [308948139](Players/308948139/README.md) | <img src="https://www.opendota.com/assets/images/dota2/rpg/portraits/default.png" width="32" height="32"/> | 0/0 | - | - | - | [View](Players/308948139/Rampages.md) |

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
