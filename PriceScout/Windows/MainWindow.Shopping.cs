using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace PriceScout.Windows;

// "Shopping list" tab: what to buy, grouped by the world where it's cheapest.
public partial class MainWindow
{
    private sealed record PlanLine(uint ItemId, string World, int Quantity, int Wanted, long Cost);

    private sealed class ShoppingPlan
    {
        public List<PlanLine> Lines { get; } = new();
        public List<uint> Unavailable { get; } = new();
        public string Scope { get; init; } = string.Empty;
    }

    private readonly AsyncLoad<ShoppingPlan> shoppingPlan = new();
    private int planRequest;

    public void AddToShoppingList(uint itemId, int quantity)
    {
        var list = plugin.Configuration.ShoppingList;
        var entry = list.FirstOrDefault(e => e.ItemId == itemId);
        if (entry == null)
            list.Add(new ShoppingEntry { ItemId = itemId, Quantity = quantity });
        else
            entry.Quantity += quantity;

        plugin.Configuration.Save();
    }

    private void DrawShoppingList()
    {
        var config = plugin.Configuration;
        var list = config.ShoppingList;

        if (list.Count == 0)
        {
            ImGui.TextWrapped("Your shopping list is empty. Look up an item and click \"+ Shopping list\", or use \"Add ingredients to shopping list\" on the Crafting profit tab.");
            return;
        }

        var scope = plugin.ResolveScope();
        using (ImRaii.Disabled(scope == null || shoppingPlan.Loading))
        {
            if (ImGui.Button(shoppingPlan.Loading ? "Finding prices..." : "Find cheapest worlds") && scope != null)
                StartPlan(scope);
        }
        ImGui.SameLine();
        if (ImGui.Button("Clear list"))
        {
            list.Clear();
            config.Save();
            return;
        }
        ImGui.SameLine();
        ImGui.TextDisabled(scope != null ? $"Searching {scope}" : "Log in to search prices.");

        DrawShoppingEntries(list);

        ImGui.Separator();
        DrawShoppingPlan();
    }

    private void DrawShoppingEntries(List<ShoppingEntry> list)
    {
        using var table = ImRaii.Table("##shoppingEntries", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Quantity", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 24 * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        ShoppingEntry? toRemove = null;
        foreach (var entry in list)
        {
            if (!plugin.MarketItems.TryGetValue(entry.ItemId, out var item))
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawItemIcon(item, 20);
            ImGui.SameLine();
            if (ImGui.Selectable($"{item.Name}##shop{item.Id}"))
                ShowItem(item);

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var quantity = entry.Quantity;
            if (ImGui.InputInt($"##qty{item.Id}", ref quantity))
            {
                entry.Quantity = Math.Clamp(quantity, 1, 9999);
                plugin.Configuration.Save();
            }

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##shopRemove{item.Id}"))
                toRemove = entry;
        }

        if (toRemove != null)
        {
            list.Remove(toRemove);
            plugin.Configuration.Save();
        }
    }

    private void StartPlan(string scope)
    {
        var wanted = plugin.Configuration.ShoppingList.ToDictionary(e => e.ItemId, e => e.Quantity);
        planRequest++;
        shoppingPlan.Start($"plan:{planRequest}", async token =>
        {
            var markets = await plugin.Universalis.GetMarketDataBatchAsync(scope, wanted.Keys, 100, 0, token).ConfigureAwait(false);
            return BuildPlan(scope, wanted, markets);
        });
    }

    // For each item, pick the single world where buying the full amount costs least.
    // If no world has enough, use the world with the most stock and flag the shortfall.
    private static ShoppingPlan BuildPlan(string scope, Dictionary<uint, int> wanted, Dictionary<uint, MarketData> markets)
    {
        var plan = new ShoppingPlan { Scope = scope };
        foreach (var (itemId, quantity) in wanted)
        {
            if (!markets.TryGetValue(itemId, out var market) || market.Listings.Count == 0)
            {
                plan.Unavailable.Add(itemId);
                continue;
            }

            var options = market.Listings
                .GroupBy(l => l.WorldName ?? "?")
                .Select(g => CostOnWorld(g.Key, g.OrderBy(l => l.PricePerUnit), quantity))
                .ToList();

            var best = options.Where(o => o.Quantity >= quantity).MinBy(o => o.Cost)
                ?? options.MaxBy(o => o.Quantity)!;
            plan.Lines.Add(best with { ItemId = itemId, Wanted = quantity });
        }

        return plan;
    }

    // Buys whole listings (the market board doesn't split stacks), cheapest first, until the amount is covered.
    private static PlanLine CostOnWorld(string world, IEnumerable<Listing> listings, int quantity)
    {
        var bought = 0;
        long cost = 0;
        foreach (var listing in listings)
        {
            if (bought >= quantity)
                break;

            bought += listing.Quantity;
            cost += listing.Total > 0 ? listing.Total : listing.PricePerUnit * listing.Quantity;
        }

        return new PlanLine(0, world, bought, quantity, cost);
    }

    private void DrawShoppingPlan()
    {
        if (shoppingPlan.Key == null)
        {
            ImGui.TextDisabled("Click \"Find cheapest worlds\" to plan where to buy everything.");
            return;
        }

        if (shoppingPlan.Loading)
            return;

        if (shoppingPlan.Error != null)
        {
            ImGui.TextColored(ErrorColor, $"Could not load prices: {shoppingPlan.Error}");
            return;
        }

        var plan = shoppingPlan.Value;
        if (plan == null)
            return;

        using var child = ImRaii.Child("##plan", new System.Numerics.Vector2(-1, -1));
        if (!child.Success)
            return;

        foreach (var group in plan.Lines.GroupBy(l => l.World).OrderByDescending(g => g.Sum(l => l.Cost)))
        {
            var subtotal = group.Sum(l => l.Cost);
            ImGui.TextColored(HereColor, $"{group.Key}  -  {FormatGil(subtotal)}");
            using (ImRaii.PushIndent())
            {
                foreach (var line in group)
                {
                    var name = plugin.MarketItems.TryGetValue(line.ItemId, out var item) ? item.Name : $"Item {line.ItemId}";
                    var extra = line.Quantity > line.Wanted ? $" (buys {line.Quantity}, stacks can't be split)" : string.Empty;
                    if (line.Quantity < line.Wanted)
                        ImGui.TextColored(StaleColor, $"{name} x{line.Quantity} of {line.Wanted} - only {line.Quantity} for sale here  -  {FormatGil(line.Cost)}");
                    else
                        ImGui.Text($"{name} x{line.Wanted}{extra}  -  {FormatGil(line.Cost)}");
                }
            }
            ImGui.Spacing();
        }

        foreach (var itemId in plan.Unavailable)
        {
            var name = plugin.MarketItems.TryGetValue(itemId, out var item) ? item.Name : $"Item {itemId}";
            ImGui.TextColored(ErrorColor, $"{name}: no listings on {plan.Scope}");
        }

        ImGui.Separator();
        ImGui.Text($"Total: {FormatGil(plan.Lines.Sum(l => l.Cost))} across {plan.Lines.Select(l => l.World).Distinct().Count()} worlds");
    }
}
