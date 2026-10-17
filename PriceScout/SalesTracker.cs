using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Chat;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Interface.ImGuiNotification;
using Dalamud.Plugin.Services;

namespace PriceScout;

// Records retainer sales from three sources:
//  - the game's sale chat message while you're online,
//  - comparing each retainer's sell list with the last time you opened it (sales made while offline),
//  - listing counts at the summoning bell (a quick "something sold" heads-up).
public sealed class SalesTracker : IDisposable
{
    private const string SellListAddon = "RetainerSellList";
    private const string RetainerListAddon = "RetainerList";
    private const int MaxLogEntries = 500;

    private static readonly Regex GilAmount = new(@"([\d][\d,.]*)\s*gil", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Plugin plugin;
    private readonly CancellationTokenSource cts = new();

    private bool sellListOpen;
    private bool comparedThisVisit;
    private DateTime sellListOpenedAt;
    private DateTime nextScan;

    public SalesTracker(Plugin plugin)
    {
        this.plugin = plugin;
        Plugin.ChatGui.ChatMessage += OnChatMessage;
        Plugin.Framework.Update += OnFrameworkUpdate;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, SellListAddon, OnSellListOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, SellListAddon, OnSellListClosed);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, RetainerListAddon, OnRetainerListOpened);
    }

    public void Dispose()
    {
        Plugin.ChatGui.ChatMessage -= OnChatMessage;
        Plugin.Framework.Update -= OnFrameworkUpdate;
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, SellListAddon, OnSellListOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SellListAddon, OnSellListClosed);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, RetainerListAddon, OnRetainerListOpened);
        cts.Cancel();
        cts.Dispose();
    }

    private Configuration Config => plugin.Configuration;

    // Retainer names are only unique per character, so key everything by character too.
    private static string? CharacterKey()
    {
        if (!Plugin.PlayerState.IsLoaded || Plugin.PlayerState.ContentId == 0)
            return null;
        return Plugin.PlayerState.ContentId.ToString("X");
    }

    private static string? RetainerKey(string retainerName)
    {
        var character = CharacterKey();
        return character == null ? null : $"{character}/{retainerName}";
    }

    private static string RetainerNameFromKey(string key) => key[(key.IndexOf('/') + 1)..];

    // ----- Live sales (chat message) -----

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        if (chatMessage.LogKind != XivChatType.RetainerSale)
            return;

        SeString message = chatMessage.Message;
        var itemPayload = message.Payloads.OfType<ItemPayload>().FirstOrDefault();
        if (itemPayload == null)
            return;

        var itemId = itemPayload.ItemId % 500_000;
        var hq = itemPayload.IsHQ;
        long? total = null;
        var match = GilAmount.Match(message.TextValue);
        if (match.Success && long.TryParse(match.Groups[1].Value.Replace(",", string.Empty).Replace(".", string.Empty), out var gil))
            total = gil;

        // Find which retainer it was by matching the remembered sell lists, and forget that listing
        // so it isn't counted again the next time that sell list is opened.
        var tracked = TakeTrackedListing(itemId, hq, total);

        Record(new SaleRecord
        {
            ItemId = itemId,
            Hq = hq,
            Quantity = tracked?.Listing.Quantity,
            UnitPrice = tracked?.Listing.UnitPrice,
            Total = total,
            Time = DateTime.UtcNow,
            Retainer = tracked != null ? RetainerNameFromKey(tracked.Value.Key) : null,
            Source = SaleSource.Live,
        });

        Notify("Item sold!", $"{ItemName(itemId, hq)} sold{(total.HasValue ? $" for {total.Value:N0} gil" : string.Empty)}.");
    }

    private (string Key, TrackedListing Listing)? TakeTrackedListing(uint itemId, bool hq, long? total)
    {
        var character = CharacterKey();
        if (character == null)
            return null;

        var candidates = Config.RetainerListings
            .Where(kv => kv.Key.StartsWith(character + "/", StringComparison.Ordinal))
            .SelectMany(kv => kv.Value.Where(l => l.ItemId == itemId && l.Hq == hq).Select(l => (kv.Key, Listing: l)))
            .ToList();
        if (candidates.Count == 0)
            return null;

        // The sale message shows the total after the 5% fee, so prefer the listing whose price fits it best.
        var best = total.HasValue
            ? candidates.MinBy(c => Math.Abs(c.Listing.UnitPrice * c.Listing.Quantity * 0.95 - total.Value))
            : candidates[0];

        Config.RetainerListings[best.Key].Remove(best.Listing);
        Config.Save();
        return best;
    }

    // ----- Offline sales (sell list comparison) -----

    private void OnSellListOpened(AddonEvent type, AddonArgs args)
    {
        sellListOpen = true;
        comparedThisVisit = false;
        sellListOpenedAt = DateTime.UtcNow;
        nextScan = DateTime.MinValue;
    }

    private void OnSellListClosed(AddonEvent type, AddonArgs args)
    {
        // Save the final state of the list, including any changes made during this visit.
        if (sellListOpen && comparedThisVisit)
            ScanSellList();
        sellListOpen = false;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!sellListOpen || DateTime.UtcNow < nextScan)
            return;

        nextScan = DateTime.UtcNow.AddSeconds(1);

        // Give the game a moment to load the sell list after the window opens.
        if (DateTime.UtcNow - sellListOpenedAt < TimeSpan.FromSeconds(1))
            return;

        ScanSellList();
    }

    private void ScanSellList()
    {
        var retainerName = RetainerReader.ActiveRetainerName();
        var key = retainerName == null ? null : RetainerKey(retainerName);
        if (key == null)
            return;

        var current = RetainerReader.ActiveSellList();

        // Only trust the read once it agrees with the retainer's own listing count.
        var expected = RetainerReader.ActiveRetainerListedCount();
        if (expected >= 0 && current.Count != expected)
            return;

        var previous = Config.RetainerListings.GetValueOrDefault(key) ?? new List<TrackedListing>();

        if (!comparedThisVisit)
        {
            comparedThisVisit = true;

            var missing = previous.Where(p => !current.Any(c => Same(c, p))).ToList();
            if (missing.Count > 0)
                _ = ConfirmOfflineSales(retainerName!, missing);
        }

        // This runs every second while the sell list is open, so only touch the saved config when the list changed.
        var unchanged = previous.Count == current.Count && current.All(c => previous.Any(p => Same(c, p)));
        if (unchanged && Config.RetainerListingCounts.GetValueOrDefault(key, -1) == current.Count)
            return;

        // Remember the list as it is now. Listings that were already there keep their original time.
        Config.RetainerListings[key] = current
            .Select(c => previous.FirstOrDefault(p => Same(c, p)) ?? new TrackedListing
            {
                ItemId = c.ItemId,
                Hq = c.Hq,
                Quantity = c.Quantity,
                UnitPrice = c.Price,
                FirstSeen = DateTime.UtcNow,
            })
            .ToList();
        // Keep the bell count in step too, so items you take down yourself aren't reported as sold.
        Config.RetainerListingCounts[key] = current.Count;
        Config.Save();
    }

    private static bool Same(OwnListing current, TrackedListing tracked) =>
        current.ItemId == tracked.ItemId && current.Hq == tracked.Hq &&
        current.Quantity == tracked.Quantity && current.Price == tracked.UnitPrice;

    // Checks Universalis sale history for each listing that disappeared while you were away.
    private async Task ConfirmOfflineSales(string retainerName, List<TrackedListing> missing)
    {
        var world = plugin.CurrentLocation?.World;
        var records = new List<SaleRecord>();

        foreach (var listing in missing)
        {
            SaleRecord record = new()
            {
                ItemId = listing.ItemId,
                Hq = listing.Hq,
                Quantity = listing.Quantity,
                UnitPrice = listing.UnitPrice,
                Total = listing.UnitPrice * listing.Quantity,
                Time = DateTime.UtcNow,
                Retainer = retainerName,
                Source = SaleSource.Unconfirmed,
            };

            if (world != null)
            {
                try
                {
                    var days = Math.Clamp((int)Math.Ceiling((DateTime.UtcNow - listing.FirstSeen).TotalDays) + 1, 1, 30);
                    var history = await plugin.Universalis.GetHistoryAsync(world, listing.ItemId, days, cts.Token).ConfigureAwait(false);
                    var since = new DateTimeOffset(listing.FirstSeen).ToUnixTimeSeconds();
                    var sale = history.FirstOrDefault(s =>
                        s.Hq == listing.Hq && s.Quantity == listing.Quantity &&
                        s.PricePerUnit == listing.UnitPrice && s.Timestamp >= since);

                    if (sale != null)
                    {
                        record.Source = SaleSource.Confirmed;
                        record.Time = DateTimeOffset.FromUnixTimeSeconds(sale.Timestamp).UtcDateTime;
                        record.Buyer = sale.BuyerName;
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, $"Could not check sale history for item {listing.ItemId}");
                }
            }

            records.Add(record);
        }

        await Plugin.Framework.RunOnFrameworkThread(() =>
        {
            foreach (var record in records)
                Record(record);

            var confirmed = records.Count(r => r.Source == SaleSource.Confirmed);
            var gil = records.Where(r => r.Source == SaleSource.Confirmed).Sum(r => r.Total ?? 0);
            var summary = confirmed > 0
                ? $"{retainerName} sold {confirmed} listing(s) for {gil:N0} gil while you were away."
                : $"{retainerName} has {records.Count} listing(s) gone since last time.";
            if (records.Count > confirmed && confirmed > 0)
                summary += $" {records.Count - confirmed} more are gone but couldn't be confirmed as sold.";

            Plugin.ChatGui.Print($"[PriceScout] {summary} See the Sales tab for details.");
            Notify("Retainer sales", summary);
        }).ConfigureAwait(false);
    }

    // ----- Summoning bell heads-up -----

    private void OnRetainerListOpened(AddonEvent type, AddonArgs args)
    {
        var character = CharacterKey();
        if (character == null)
            return;

        var changes = new List<string>();
        foreach (var retainer in RetainerReader.AllRetainers())
        {
            var key = $"{character}/{retainer.Name}";
            if (Config.RetainerListingCounts.TryGetValue(key, out var before) && retainer.ListedItems < before)
                changes.Add($"{retainer.Name}: {before - retainer.ListedItems}");
            Config.RetainerListingCounts[key] = retainer.ListedItems;
        }
        Config.Save();

        if (changes.Count > 0)
        {
            var message = $"Listings gone since last time (sold or expired): {string.Join(", ", changes)}. Open each retainer's sell list to see what sold.";
            Plugin.ChatGui.Print($"[PriceScout] {message}");
            Notify("Your retainers sold something", message);
        }
    }

    // ----- Shared -----

    private void Record(SaleRecord record)
    {
        Config.SaleLog.Insert(0, record);
        if (Config.SaleLog.Count > MaxLogEntries)
            Config.SaleLog.RemoveRange(MaxLogEntries, Config.SaleLog.Count - MaxLogEntries);
        Config.Save();
    }

    private void Notify(string title, string content)
    {
        if (!Config.SaleNotificationsEnabled)
            return;

        Plugin.NotificationManager.AddNotification(new Notification
        {
            Title = title,
            Content = content,
            Type = NotificationType.Success,
        });
    }

    private string ItemName(uint itemId, bool hq)
    {
        var name = plugin.MarketItems.TryGetValue(itemId, out var item) ? item.Name : $"Item {itemId}";
        return hq ? $"{name} (HQ)" : name;
    }
}
