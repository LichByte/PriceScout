<img src="images/icon.png" alt="" width="96" align="right">

# PriceScout

A [Dalamud](https://github.com/goatcorp/Dalamud) plugin for Final Fantasy XIV that shows market board prices across worlds without leaving the game. Market data comes from [Universalis](https://universalis.app).

## Features

- **Price lookup:** the cheapest NQ and HQ listing on every world in your data center or region, with how fresh each world's data is. Your current and home worlds are highlighted.
- **Automatic location:** detects your world, data center and region, and reloads prices when you travel. You can also type any data center, world or region name.
- **Right-click "Check Prices":** on items in your inventory, chat links, the crafting log, market board results, shops and more.
- **Prices on tooltips:** a small box next to the game's item tooltip with the cheapest price and average sale.
- **Popular items:** what's actively selling, ranked by gil traded per day. Browse trending items or a whole market board category.
- **Watchlist:** track items and get a chat message and notification when the price drops to your target.
- **Price history:** a 7, 14 or 30-day chart of average daily sale prices.
- **Crafting profit:** ingredient cost from the cheapest listings compared with what the result sells for.
- **Shopping list:** groups the items you need by the world where they're cheapest.
- **Undercut checker:** opens with your retainer's sell list and flags listings that someone has undercut.
- **Sales tracker:** logs retainer sales live, spots sales made while you were offline, and totals your earnings.

## Installing

PriceScout is installed from a custom plugin repository:

1. In game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this URL, click **+**, tick **Enabled**, then **Save and Close**:
   ```
   https://raw.githubusercontent.com/LichByte/PriceScout/main/repo.json
   ```
3. Type `/xlplugins`, search for **PriceScout** and click **Install**.

Updates arrive through the plugin installer like any other plugin.

## Getting started

- Type `/pricescout` (or `/psc`) to open the window, or right-click any item and choose **Check Prices**.
- Prices follow you: PriceScout uses your current data center (or region, if you pick **My region**) and reloads when you travel.
- To track retainer sales, open each retainer's sell list once so PriceScout knows what's listed. The undercut checker opens beside the sell list automatically.
- Settings are on the **Settings** tab, or the ⚙ button in the plugin installer.

## Commands

| Command | What it does |
|---|---|
| `/pricescout` | Open or close the PriceScout window |
| `/pricescout <item name>` | Look up an item's prices |
| `/psc` | Short alias for `/pricescout` |

## Building

Requirements:
- [.NET SDK 10](https://dotnet.microsoft.com/download)
- [XIVLauncher](https://goatcorp.github.io/) with Dalamud installed (the build uses Dalamud's libraries from your XIVLauncher folder)

```
cd PriceScout
dotnet build -c Release
```

The plugin is built to `PriceScout/bin/Release/PriceScout.dll`.

## Installing a local build as a dev plugin

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Dev Plugin Locations**, add the full path to `PriceScout.dll`, click **+**, then **Save and Close**.
3. Open `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** and enable **PriceScout**.

After rebuilding, use the reload button next to PriceScout in Dev Tools. There's no need to restart the game.

## Notes

- Universalis data comes from players who use market board upload plugins, so a world's prices are only as recent as its last upload. The **Updated** column shows how old each world's data is.
- Region-wide sale history is slow on Universalis and sometimes times out. Use **My data center** if the Recent sales or Price history tabs fail.
- Offline sales are found by comparing each retainer's sell list with the last time you opened it. Open each retainer's sell list once to start tracking.
- Settings, the watchlist, the shopping list and the sales log are saved by Dalamud in `%APPDATA%\XIVLauncher\pluginConfigs\PriceScout.json`.
- PriceScout only reads data. It never buys, lists or changes prices for you.
- Item lookups are sent to [Universalis](https://universalis.app). Nothing about your character is sent.
- Third-party tools like Dalamud are against the FFXIV Terms of Service. Use at your own risk.

## Releasing

1. Bump `<Version>` in `PriceScout/PriceScout.csproj` and add an entry to `CHANGELOG.md`.
2. Commit, then tag and push: `git tag v1.0.0` and `git push origin main v1.0.0`.
3. The **Release** GitHub Action builds the plugin, attaches `latest.zip` to a GitHub release and updates `repo.json`.

To regenerate `repo.json` locally after a Release build: `pwsh tools/make-repo-json.ps1`.

## License

[AGPL-3.0](LICENSE)

## Project layout

| File | Purpose |
|---|---|
| `Plugin.cs` | Entry point: commands, context menus, location detection |
| `UniversalisClient.cs` | Universalis API calls, caching and retries |
| `Watchlist.cs` | Background price checks and alerts |
| `SalesTracker.cs` | Live and offline retainer sale detection |
| `RetainerReader.cs` | Reads retainer listings from game memory |
| `Recipes.cs` | Recipe lookup from game data |
| `Configuration.cs` | Saved settings and data |
| `Windows/` | The main window and its tabs, tooltip prices and the undercut checker |
| `tools/make-repo-json.ps1` | Builds the custom repo file (`repo.json`) from the plugin manifest |
| `.github/workflows/release.yml` | Builds and publishes a release when a version tag is pushed |
