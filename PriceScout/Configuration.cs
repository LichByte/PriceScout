using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace PriceScout;

public enum SearchScope
{
    DataCenter,
    Region,
}

[Serializable]
public class WatchedItem
{
    public uint ItemId { get; set; }

    // Alert when the cheapest listing is at or below this price. 0 turns the alert off.
    public int AlertBelow { get; set; }
}

[Serializable]
public class ShoppingEntry
{
    public uint ItemId { get; set; }
    public int Quantity { get; set; } = 1;
}

[Serializable]
public class TrackedListing
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; }
    public int Quantity { get; set; }
    public long UnitPrice { get; set; }
    public DateTime FirstSeen { get; set; }
}

public enum SaleSource
{
    // The game's "your item has sold" chat message while you were online.
    Live,
    // Gone from the sell list since last time, and Universalis has a matching sale.
    Confirmed,
    // Gone from the sell list since last time, but no matching sale was found (it may have been sold or taken back).
    Unconfirmed,
}

[Serializable]
public class SaleRecord
{
    public uint ItemId { get; set; }
    public bool Hq { get; set; }
    public int? Quantity { get; set; }
    public long? UnitPrice { get; set; }
    public long? Total { get; set; }
    public DateTime Time { get; set; }
    public string? Retainer { get; set; }
    public string? Buyer { get; set; }
    public SaleSource Source { get; set; }
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public SearchScope Scope { get; set; } = SearchScope.DataCenter;

    // Empty means "use the data center/region of the world the player is on".
    public string ScopeOverride { get; set; } = string.Empty;

    public int ListingCount { get; set; } = 50;
    public int HistoryCount { get; set; } = 20;

    public List<WatchedItem> Watchlist { get; set; } = new();
    public int WatchRefreshMinutes { get; set; } = 5;
    public bool PriceAlertsEnabled { get; set; } = true;

    public List<ShoppingEntry> ShoppingList { get; set; } = new();

    public bool ShowTooltipPrices { get; set; } = true;
    public bool UndercutCheckerEnabled { get; set; } = true;
    public int HistoryDays { get; set; } = 14;

    // Last known sell list for each retainer ("Character@World/Retainer"), used to spot sales made while offline.
    public Dictionary<string, List<TrackedListing>> RetainerListings { get; set; } = new();
    // Last known number of items each retainer had listed, as shown at the summoning bell.
    public Dictionary<string, int> RetainerListingCounts { get; set; } = new();
    public List<SaleRecord> SaleLog { get; set; } = new();
    public bool SaleNotificationsEnabled { get; set; } = true;

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
