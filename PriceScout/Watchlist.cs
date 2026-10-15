using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace PriceScout;

public sealed class WatchResult
{
    public long CheapestPrice { get; init; }
    public bool CheapestIsHq { get; init; }
    public string? CheapestWorld { get; init; }
    public int ListingCount { get; init; }
    public DateTime FetchedAt { get; init; }
    public string? Error { get; init; }
}

// Periodically fetches the cheapest listing for every watched item and raises alerts.
public sealed class Watchlist : IDisposable
{
    private const int ListingsPerItem = 10;

    private readonly Plugin plugin;
    private readonly ConcurrentDictionary<uint, WatchResult> results = new();
    // Items whose alert already fired; cleared once the price goes back above the threshold.
    private readonly ConcurrentDictionary<uint, bool> alerted = new();
    private readonly CancellationTokenSource cts = new();

    private DateTime nextRefresh = DateTime.MinValue;
    private volatile bool refreshing;

    public Watchlist(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.Framework.Update += OnFrameworkUpdate;
    }

    public void Dispose()
    {
        Plugin.Framework.Update -= OnFrameworkUpdate;
        cts.Cancel();
        cts.Dispose();
    }

    public bool IsRefreshing => refreshing;
    public DateTime NextRefresh => nextRefresh;

    public WatchResult? GetResult(uint itemId) => results.GetValueOrDefault(itemId);

    public bool Contains(uint itemId) => plugin.Configuration.Watchlist.Any(w => w.ItemId == itemId);

    public void Add(uint itemId)
    {
        if (Contains(itemId))
            return;

        plugin.Configuration.Watchlist.Add(new WatchedItem { ItemId = itemId });
        plugin.Configuration.Save();
        RefreshNow();
    }

    public void Remove(uint itemId)
    {
        plugin.Configuration.Watchlist.RemoveAll(w => w.ItemId == itemId);
        plugin.Configuration.Save();
        results.TryRemove(itemId, out _);
        alerted.TryRemove(itemId, out _);
    }

    public void RefreshNow() => nextRefresh = DateTime.MinValue;

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (refreshing || DateTime.UtcNow < nextRefresh)
            return;

        var config = plugin.Configuration;
        var items = config.Watchlist.Select(w => (w.ItemId, w.AlertBelow)).ToList();
        var scope = plugin.ResolveScope();
        nextRefresh = DateTime.UtcNow.AddMinutes(Math.Max(1, config.WatchRefreshMinutes));

        if (items.Count == 0 || scope == null)
            return;

        refreshing = true;
        var token = cts.Token;
        Task.Run(async () =>
        {
            try
            {
                foreach (var (itemId, alertBelow) in items)
                {
                    if (token.IsCancellationRequested)
                        return;

                    results[itemId] = await FetchOne(scope, itemId, token).ConfigureAwait(false);
                    CheckAlert(itemId, alertBelow, results[itemId]);

                    // Be polite to Universalis.
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                refreshing = false;
            }
        }, token);
    }

    private async Task<WatchResult> FetchOne(string scope, uint itemId, CancellationToken token)
    {
        try
        {
            var data = await plugin.Universalis
                .GetMarketDataAsync(scope, itemId, ListingsPerItem, 0, token)
                .ConfigureAwait(false);

            var cheapest = data?.Listings.MinBy(l => l.PricePerUnit);
            return new WatchResult
            {
                CheapestPrice = cheapest?.PricePerUnit ?? 0,
                CheapestIsHq = cheapest?.Hq ?? false,
                CheapestWorld = cheapest?.WorldName,
                ListingCount = data?.Listings.Count ?? 0,
                FetchedAt = DateTime.UtcNow,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Plugin.Log.Warning(ex, $"Watchlist fetch failed for item {itemId}");
            return new WatchResult { FetchedAt = DateTime.UtcNow, Error = ex.Message };
        }
    }

    private void CheckAlert(uint itemId, int alertBelow, WatchResult result)
    {
        if (alertBelow <= 0 || result.Error != null || result.CheapestPrice <= 0)
            return;

        if (result.CheapestPrice > alertBelow)
        {
            alerted.TryRemove(itemId, out _);
            return;
        }

        if (!plugin.Configuration.PriceAlertsEnabled || !alerted.TryAdd(itemId, true))
            return;

        var name = plugin.MarketItems.TryGetValue(itemId, out var item) ? item.Name : $"Item {itemId}";
        var hq = result.CheapestIsHq ? " (HQ)" : string.Empty;
        var message = $"{name}{hq} is {result.CheapestPrice:N0} gil on {result.CheapestWorld} (your alert: {alertBelow:N0} gil)";

        Plugin.Framework.RunOnFrameworkThread(() =>
        {
            Plugin.ChatGui.Print($"[PriceScout] {message}");
            Plugin.NotificationManager.AddNotification(new Notification
            {
                Title = "PriceScout price alert",
                Content = message,
                Type = NotificationType.Info,
            });
        });
    }
}
