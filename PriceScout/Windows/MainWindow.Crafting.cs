using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace PriceScout.Windows;

// "Crafting profit" tab: ingredient cost from the cheapest listings vs. what the result sells for.
public partial class MainWindow
{
    private enum SellPriceBasis
    {
        CheapestHq,
        CheapestNq,
        AverageSale,
    }

    private readonly AsyncLoad<Dictionary<uint, MarketData>> ingredientPrices = new();
    private int recipeIndex;
    private int craftCount = 1;
    private SellPriceBasis sellBasis = SellPriceBasis.CheapestHq;

    private void DrawCrafting(MarketItem item, MarketData resultMarket)
    {
        var recipes = Recipes.ForItem(item.Id);
        recipeIndex = Math.Clamp(recipeIndex, 0, recipes.Count - 1);

        if (recipes.Count > 1)
        {
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var labels = recipes.Select(r => $"{r.Job} lv{r.Level}").ToArray();
            ImGui.Combo("Recipe", ref recipeIndex, labels, labels.Length);
        }

        var recipe = recipes[recipeIndex];
        var scope = loadedScope;
        if (scope == null)
            return;

        var ingredientIds = recipe.Ingredients.Select(i => i.ItemId).Where(plugin.MarketItems.ContainsKey).ToList();
        ingredientPrices.Ensure($"{scope}:{recipe.RecipeId}", token =>
            plugin.Universalis.GetMarketDataBatchAsync(scope, ingredientIds, 100, 0, token));

        if (ingredientPrices.Loading)
        {
            ImGui.Text("Loading ingredient prices...");
            return;
        }

        if (ingredientPrices.Error != null)
        {
            ImGui.TextColored(ErrorColor, $"Could not load ingredient prices: {ingredientPrices.Error}");
            return;
        }

        var prices = ingredientPrices.Value ?? new Dictionary<uint, MarketData>();

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Crafts:");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("##craftCount", ref craftCount))
            craftCount = Math.Clamp(craftCount, 1, 999);
        ImGui.SameLine();
        ImGui.TextDisabled($"({recipe.Job} lv{recipe.Level}, makes {recipe.AmountResult} each)");

        long totalCost = 0;
        var missingPrice = false;
        var shortStock = false;

        using (var table = ImRaii.Table("##ingredients", 5, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH))
        {
            if (table.Success)
            {
                ImGui.TableSetupColumn("Ingredient", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Need");
                ImGui.TableSetupColumn("Price each");
                ImGui.TableSetupColumn("World");
                ImGui.TableSetupColumn("Subtotal");
                ImGui.TableHeadersRow();

                foreach (var ingredient in recipe.Ingredients)
                {
                    var need = ingredient.Amount * craftCount;
                    // Priced the same way as the shopping list: whole stacks, on the one world where they cost least.
                    var cheapest = prices.TryGetValue(ingredient.ItemId, out var market) ? BestWorld(market, need) : null;

                    ImGui.TableNextRow();
                    ImGui.TableNextColumn();
                    if (plugin.MarketItems.TryGetValue(ingredient.ItemId, out var ingredientItem))
                    {
                        if (ImGui.Selectable($"{ingredient.Name}##ing{ingredient.ItemId}"))
                            ShowItem(ingredientItem);
                    }
                    else
                    {
                        ImGui.Text(ingredient.Name);
                    }

                    ImGui.TableNextColumn();
                    ImGui.Text(need.ToString());
                    ImGui.TableNextColumn();
                    if (cheapest != null)
                    {
                        ImGui.Text(FormatGil((double)cheapest.Cost / Math.Max(1, cheapest.Quantity)));
                        if (cheapest.Quantity > need && ImGui.IsItemHovered())
                            ImGui.SetTooltip($"Average over the {cheapest.Quantity} you'd have to buy (stacks can't be split).");
                        ImGui.TableNextColumn();
                        ImGui.Text(cheapest.World);
                        ImGui.TableNextColumn();
                        if (cheapest.Quantity < need)
                        {
                            // Not enough for sale on any one world: estimate the rest at the same average price.
                            shortStock = true;
                            var estimate = (long)Math.Round((double)cheapest.Cost / cheapest.Quantity * need);
                            totalCost += estimate;
                            ImGui.TextColored(StaleColor, $"~{FormatGil(estimate)}");
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip($"Only {cheapest.Quantity} for sale on {cheapest.World}; the rest is estimated at the same price.");
                        }
                        else
                        {
                            totalCost += cheapest.Cost;
                            ImGui.Text(FormatGil(cheapest.Cost));
                        }
                    }
                    else
                    {
                        missingPrice = true;
                        ImGui.TextDisabled(ingredientItem == null ? "not sold" : "no listings");
                        ImGui.TableNextColumn();
                        ImGui.TableNextColumn();
                    }
                }
            }
        }

        if (missingPrice)
            ImGui.TextColored(StaleColor, "Some ingredients can't be bought on the market, so the cost below leaves them out.");
        if (shortStock)
            ImGui.TextColored(StaleColor, "No single world has enough of some ingredients; their cost is an estimate.");

        ImGui.Separator();

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Sell at:");
        ImGui.SameLine();
        var basis = (int)sellBasis;
        if (item.CanBeHq)
        {
            if (ImGui.RadioButton("Cheapest HQ listing", ref basis, (int)SellPriceBasis.CheapestHq))
                sellBasis = SellPriceBasis.CheapestHq;
            ImGui.SameLine();
        }
        else if (sellBasis == SellPriceBasis.CheapestHq)
        {
            sellBasis = SellPriceBasis.CheapestNq;
            basis = (int)sellBasis;
        }
        if (ImGui.RadioButton(item.CanBeHq ? "Cheapest NQ listing" : "Cheapest listing", ref basis, (int)SellPriceBasis.CheapestNq))
            sellBasis = SellPriceBasis.CheapestNq;
        ImGui.SameLine();
        if (ImGui.RadioButton("Average sale", ref basis, (int)SellPriceBasis.AverageSale))
            sellBasis = SellPriceBasis.AverageSale;

        var unitPrice = sellBasis switch
        {
            SellPriceBasis.CheapestHq => resultMarket.Listings.Where(l => l.Hq).Select(l => (double?)l.PricePerUnit).Min(),
            SellPriceBasis.CheapestNq => resultMarket.Listings.Where(l => !l.Hq).Select(l => (double?)l.PricePerUnit).Min(),
            _ => item.CanBeHq && resultMarket.AveragePriceHq > 0 ? resultMarket.AveragePriceHq : resultMarket.AveragePriceNq,
        };

        var made = recipe.AmountResult * craftCount;
        ImGui.Text($"Ingredient cost: {FormatGil(totalCost)}");

        if (unitPrice is not > 0)
        {
            ImGui.TextDisabled("No price to compare against for that option.");
        }
        else
        {
            // Market board sales are taxed (up to 5%), so estimate what actually reaches you.
            var revenue = unitPrice.Value * made * 0.95;
            var profit = revenue - totalCost;
            ImGui.Text($"Sells for: {FormatGil(unitPrice.Value)} x {made} = {FormatGil(unitPrice.Value * made)}  (about {FormatGil(revenue)} after 5% tax)");

            var color = profit >= 0 ? new Vector4(0.4f, 1f, 0.4f, 1f) : ErrorColor;
            var margin = totalCost > 0 ? $"  ({profit / totalCost:P0})" : string.Empty;
            ImGui.TextColored(color, $"Profit: {(profit < 0 ? "-" : string.Empty)}{FormatGil(Math.Abs(profit))}{margin}");
        }

        ImGui.TextDisabled($"Sells about {resultMarket.SaleVelocity:0.#} per day on {scope}.");

        ImGui.Spacing();
        if (ImGui.Button("Add ingredients to shopping list"))
        {
            foreach (var ingredient in recipe.Ingredients.Where(i => plugin.MarketItems.ContainsKey(i.ItemId)))
                AddToShoppingList(ingredient.ItemId, ingredient.Amount * craftCount);
        }
    }
}
