using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace PriceScout.Windows;

// Shows a small price box beside the cursor whenever the game is showing an item tooltip.
public sealed class TooltipPrices : IDisposable
{
    // Wait this long on an item before asking Universalis, so skimming an inventory doesn't spam requests.
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(300);

    private readonly Plugin plugin;
    private readonly AsyncLoad<MarketData> prices = new();

    private uint hoveredItemId;
    private DateTime hoverStarted;

    public TooltipPrices(Plugin plugin)
    {
        this.plugin = plugin;
    }

    public void Dispose() => prices.Dispose();

    public void Draw()
    {
        if (!plugin.Configuration.ShowTooltipPrices)
            return;

        // Strip the HQ (1,000,000) and collectable (500,000) offsets.
        var itemId = (uint)(Plugin.GameGui.HoveredItem % 500_000);
        if (itemId != hoveredItemId)
        {
            hoveredItemId = itemId;
            hoverStarted = DateTime.UtcNow;
        }

        if (itemId == 0 || !plugin.MarketItems.TryGetValue(itemId, out var item))
            return;

        if (DateTime.UtcNow - hoverStarted < HoverDelay)
            return;

        var scope = plugin.ResolveScope();
        if (scope == null)
            return;

        prices.Ensure($"{scope}:{itemId}", token => LoadPrices(scope, itemId, token));
        DrawBox(item, scope);
    }

    private async System.Threading.Tasks.Task<MarketData> LoadPrices(string scope, uint itemId, System.Threading.CancellationToken token) =>
        await plugin.Universalis.GetLookupAsync(scope, itemId, 20, token).ConfigureAwait(false)
        ?? new MarketData();

    private void DrawBox(MarketItem item, string scope)
    {
        var scale = ImGuiHelpers.GlobalScale;
        ImGui.SetNextWindowPos(ImGui.GetIO().MousePos + new Vector2(20, -20) * scale, ImGuiCond.Always, new Vector2(0, 1));
        ImGui.SetNextWindowBgAlpha(0.9f);

        const ImGuiWindowFlags flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.AlwaysAutoResize |
                                       ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoFocusOnAppearing |
                                       ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoSavedSettings |
                                       ImGuiWindowFlags.NoMove;
        if (!ImGui.Begin("##PriceScoutTooltip", flags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextDisabled($"PriceScout - {scope}");

        if (prices.Loading)
        {
            ImGui.Text("Loading...");
        }
        else if (prices.Error != null)
        {
            ImGui.Text("Prices unavailable");
        }
        else if (prices.Value is { } market)
        {
            var nq = market.Listings.Where(l => !l.Hq).MinBy(l => l.PricePerUnit);
            var hq = market.Listings.Where(l => l.Hq).MinBy(l => l.PricePerUnit);

            if (nq == null && hq == null)
                ImGui.Text("No listings");
            if (nq != null)
                ImGui.Text($"{(item.CanBeHq ? "NQ" : "Cheapest")}: {nq.PricePerUnit:N0} gil ({nq.WorldName})");
            if (hq != null)
                ImGui.Text($"HQ: {hq.PricePerUnit:N0} gil ({hq.WorldName})");

            var average = item.CanBeHq && market.AveragePriceHq > 0 ? market.AveragePriceHq : market.AveragePriceNq;
            if (average > 0)
                ImGui.TextDisabled($"Avg sale {average:N0} gil, {market.SaleVelocity:0.#}/day");
        }

        ImGui.End();
    }
}
