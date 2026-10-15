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

## Installing as a dev plugin

1. In game, open `/xlsettings` → **Experimental**.
2. Under **Dev Plugin Locations**, add the full path to `PriceScout.dll`, click **+**, then **Save and Close**.
3. Open `/xlplugins` → **Dev Tools** → **Installed Dev Plugins** and enable **PriceScout**.

After rebuilding, use the reload button next to PriceScout in Dev Tools. There's no need to restart the game.

## Notes

- Universalis data comes from players who use market board upload plugins, so a world's prices are only as recent as its last upload. The **Updated** column shows how old each world's data is.
- Region-wide sale history is slow on Universalis and sometimes times out. Use **My data center** if the Recent sales or Price history tabs fail.
- Offline sales are found by comparing each retainer's sell list with the last time you opened it. Open each retainer's sell list once to start tracking.
- Settings, the watchlist, the shopping list and the sales log are saved by Dalamud in `%APPDATA%\XIVLauncher\pluginConfigs\PriceScout.json`.
- Third-party tools like Dalamud are against the FFXIV Terms of Service. Use at your own risk.

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
