# Dota 2 Rampage Tracker
This repository contains rampage tracking data for various Dota 2 players.

## Spieler hinzufügen (How to add players)
So fügst du neue Spieler zum Tracker hinzu:

- Du brauchst die Steam32-Account-ID des Spielers (die OpenDota „account_id“ – meist 8–9 Stellen, z. B. 183063377). Wie findest du sie?
  - Über OpenDota: Profil suchen und die Zahl in der URL kopieren (https://www.opendota.com/players/<account_id>).
  - Oder aus Steam64-ID umrechnen: Steam32 = Steam64 − 76561197960265728.

### Variante A: Über GitHub Actions (empfohlen)
1) Öffne dein Repository auf GitHub → Settings → Secrets and variables → Actions → New repository secret.
2) Lege/aktualisiere folgende Secrets:
	- `API_KEY` (dein OpenDota API Key)
	- `PLAYERS` (kommagetrennte Liste der Steam32-IDs, z. B. `183063377,308948139,181342370,131232145,NEUE_ID`)
3) Der tägliche Workflow „Daily Run“ fügt den/die Spieler automatisch hinzu und aktualisiert `README.md` sowie `Players/<ID>/Rampages.md`.
	- Optional kannst du den Lauf manuell unter „Actions → Daily Run → Run workflow“ starten.

Hinweis: Du musst keine Ordner selbst anlegen. Das Tool erzeugt `Players/<ID>/` automatisch und pflegt `player_directory_mapping.json` selbst.

### Variante B: Lokal testen (ohne CI)
1) Erstelle im Ordner `Dota2RampageTracker/OpenDotaRampage/` eine Datei `.env` mit:
	- `API_KEY=DEIN_API_KEY`
	- `PLAYERS=183063377,308948139,181342370,131232145,NEUE_ID`
2) Projekt lokal ausführen (Beispiel):
	- `dotnet restore Dota2RampageTracker/RampageTracker.sln`
	- `dotnet build --configuration Release Dota2RampageTracker/RampageTracker.sln`
	- `dotnet run --project Dota2RampageTracker/OpenDotaRampage/OpenDotaRampage.csproj`
3) Die Ergebnisse findest du in `Players/<ID>/Rampages.md`. Die Hauptübersicht (`README.md`) wird ebenfalls aktualisiert.

## Players
| Player Name | Profile Picture | Rampage Percentage | Win Rate (Total) | Win Rate (Unranked) | Win Rate (Ranked) | Rampage File |
|-------------|-----------------|--------------------|------------------|---------------------|-------------------|--------------|
| Zero | ![Profile Picture](https://avatars.steamstatic.com/c0a975434fc5b15f662cbe8214fc898c493b55ea_full.jpg) | 8/7688| 50.36% | 51.49% | 50.36% | [Rampages](./Players/183063377/Rampages.md) |
| Lucky | ![Profile Picture](https://avatars.steamstatic.com/1191c81a57194f64acfcda94f0fd0cb94e92eff7_full.jpg) | 28/5456| 54.22% | 55.75% | 52.50% | [Rampages](./Players/308948139/Rampages.md) |
| Xenas23 | ![Profile Picture](https://avatars.steamstatic.com/16392e7c2bf30770c48c4b989eef4a19f237d548_full.jpg) | 12/5736| 55.13% | 56.78% | 53.19% | [Rampages](./Players/181342370/Rampages.md) |
| Mupfel | ![Profile Picture](https://avatars.steamstatic.com/5975408a7d136abfeb6160943f0db7743d542d54_full.jpg) | 6/3923| 54.78% | 56.56% | 52.89% | [Rampages](./Players/131232145/Rampages.md) |
