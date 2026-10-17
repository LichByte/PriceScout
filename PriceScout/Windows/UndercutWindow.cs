using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace PriceScout.Windows;

// Opens next to a retainer's sell list and flags listings that other sellers have undercut.
public sealed class UndercutWindow : Window, IDisposable
{
    private const string SellListAddon = "RetainerSellList";

    private readonly Plugin plugin;
    private readonly AsyncLoad<Dictionary<uint, MarketData>> prices = new();

    private List<OwnListing> listings = new();
    private HashSet<string> ownRetainers = new(StringComparer.OrdinalIgnoreCase);
    private string retainerName = string.Empty;
    private DateTime nextScan = DateTime.MinValue;
    private bool placeBesideSellList;

    public UndercutWindow(Plugin plugin)
        : base("Undercut Checker##PriceScoutUndercut", ImGuiWindowFlags.AlwaysAutoResize)
    {
        this.plugin = plugin;
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, SellListAddon, OnSellListOpened);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, SellListAddon, OnSellListClosed);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, SellListAddon, OnSellListOpened);
        Plugin.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, SellListAddon, OnSellListClosed);
        prices.Dispose();
    }

    private void OnSellListOpened(AddonEvent type, AddonArgs args)
    {
        if (!plugin.Configuration.UndercutCheckerEnabled)
            return;

        listings.Clear();
        nextScan = DateTime.MinValue;
        placeBesideSellList = true;
        IsOpen = true;
    }

    private void OnSellListClosed(AddonEvent type, AddonArgs args) => IsOpen = false;

    // Each time the sell list opens, start the checker just to the right of it. After that it can be moved freely.
    public override void PreDraw()
    {
        if (!placeBesideSellList)
            return;

        var sellList = Plugin.GameGui.GetAddonByName(SellListAddon);
        if (sellList.IsNull || !sellList.IsVisible)
            return;

        placeBesideSellList = false;
        var position = ImGuiHelpers.MainViewport.Pos + new Vector2(sellList.X + sellList.ScaledWidth + 8 * ImGuiHelpers.GlobalScale, sellList.Y);
        ImGui.SetNextWindowPos(position);
    }

    public override void Draw()
    {
        // The sell list can change while it's open (new listings, price edits), so rescan every second.
        if (DateTime.UtcNow >= nextScan)
        {
            nextScan = DateTime.UtcNow.AddSeconds(1);
            ScanRetainer();
        }

        // Retainers sell on the world you're standing on (your home world).
        var world = plugin.CurrentLocation?.World;
        if (world == null)
        {
            ImGui.Text("Log in to check prices.");
            return;
        }

        ImGui.Text($"{retainerName} on {world}");
        if (listings.Count == 0)
        {
            ImGui.TextDisabled("This retainer has nothing listed.");
            return;
        }

        var ids = listings.Select(l => l.ItemId).Distinct().OrderBy(id => id).ToList();
        prices.Ensure($"{world}:{string.Join(',', ids)}", token =>
            plugin.Universalis.GetMarketDataBatchAsync(world, ids, 20, 0, token));

        ImGui.SameLine();
        if (ImGui.SmallButton("Refresh"))
            prices.Start($"{world}:{string.Join(',', ids)}", token =>
                plugin.Universalis.GetMarketDataBatchAsync(world, ids, 20, 0, token));

        if (prices.Loading)
        {
            ImGui.Text("Checking prices...");
            return;
        }

        if (prices.Error != null)
        {
            ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), $"Could not load prices: {prices.Error}");
            return;
        }

        DrawTable(prices.Value ?? new Dictionary<uint, MarketData>());
        ImGui.TextDisabled("Universalis data can be a few minutes old.\nOpen an item's price comparison in game to confirm before repricing.");
    }

    private void DrawTable(Dictionary<uint, MarketData> markets)
    {
        using var table = ImRaii.Table("##undercut", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.SizingFixedFit);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Your price");
        ImGui.TableSetupColumn("Lowest other");
        ImGui.TableSetupColumn("Status");
        ImGui.TableHeadersRow();

        for (var index = 0; index < listings.Count; index++)
        {
            var listing = listings[index];
            var name = plugin.MarketItems.TryGetValue(listing.ItemId, out var item) ? item.Name : $"Item {listing.ItemId}";

            // Compare against other sellers' listings of the same quality, ignoring all of your own retainers.
            var competitor = markets.TryGetValue(listing.ItemId, out var market)
                ? market.Listings
                    .Where(l => l.Hq == listing.Hq && !ownRetainers.Contains(l.RetainerName ?? string.Empty))
                    .MinBy(l => l.PricePerUnit)
                : null;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (item != null && ImGui.Selectable($"{name}{(listing.Hq ? " (HQ)" : string.Empty)}##undercut{index}"))
                plugin.ShowItem(item);
            else if (item == null)
                ImGui.Text(name);

            ImGui.TableNextColumn();
            ImGui.Text(listing.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.Text($"{listing.Price:N0}");
            ImGui.TableNextColumn();
            ImGui.Text(competitor != null ? $"{competitor.PricePerUnit:N0}" : "-");
            ImGui.TableNextColumn();

            if (competitor == null)
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Only seller");
            else if (competitor.PricePerUnit < listing.Price)
                ImGui.TextColored(new Vector4(1f, 0.5f, 0.3f, 1f), $"Undercut! Try {Math.Max(1, competitor.PricePerUnit - 1):N0}");
            else
                ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Lowest");
        }
    }

    private void ScanRetainer()
    {
        retainerName = RetainerReader.ActiveRetainerName() ?? "Retainer";
        ownRetainers = RetainerReader.AllRetainers().Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        listings = RetainerReader.ActiveSellList();
    }
}
