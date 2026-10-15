using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace PriceScout.Windows;

// "Popular" tab: what's actively selling, ranked by how much gil changes hands per day.
public partial class MainWindow
{
    private enum PopularSource
    {
        Trending,
        Category,
    }

    private enum PopularSort
    {
        GilPerDay,
        SoldPerDay,
        AveragePrice,
    }

    private sealed record PopularRow(MarketItem Item, double SoldPerDay, double AveragePrice, double GilPerDay, double? Cheapest, double HqShare);

    private const int TrendingPoolSize = 200;
    private const int MaxPopularRows = 100;

    private readonly AsyncLoad<List<PopularRow>> popular = new();
    private PopularSource popularSource = PopularSource.Trending;
    private int popularCategoryIndex;
    private PopularSort popularSort = PopularSort.GilPerDay;
    private int popularMinSold = 1;
    private int popularMinPrice;
    private string popularFilter = string.Empty;
    private int popularRequest;

    private void DrawPopular()
    {
        var scope = plugin.ResolveScope();
        var dataCenter = plugin.CurrentLocation?.DataCenter;
        if (scope == null || dataCenter == null)
        {
            ImGui.Text("Log in to a character to see what's selling.");
            return;
        }

        DrawPopularControls();

        var categories = plugin.MarketCategories;
        var category = popularSource == PopularSource.Category && categories.Count > 0
            ? categories[Math.Clamp(popularCategoryIndex, 0, categories.Count - 1)]
            : null;

        var key = $"{popularSource}:{scope}:{dataCenter}:{category?.Id}:{popularRequest}";
        popular.Ensure(key, async token =>
        {
            var ids = category != null
                ? plugin.MarketItems.Values.Where(i => i.CategoryId == category.Id).Select(i => i.Id).ToList()
                : await plugin.Universalis.GetRecentlyUpdatedItemsAsync(dataCenter, TrendingPoolSize, token).ConfigureAwait(false);

            var aggregated = await plugin.Universalis
                .GetAggregatedAsync(scope, ids.Where(plugin.MarketItems.ContainsKey), token)
                .ConfigureAwait(false);
            return aggregated.Select(ToRow).Where(r => r != null).Select(r => r!).ToList();
        });

        ImGui.TextDisabled(popularSource == PopularSource.Trending
            ? $"Items players on {dataCenter} have been checking recently, with sales stats for {scope}."
            : $"Every item in {category?.Name} with sales stats for {scope}.");
        ImGui.Separator();

        if (popular.Loading)
        {
            ImGui.Text("Loading sales stats...");
            return;
        }

        if (popular.Error != null)
        {
            ImGui.TextColored(ErrorColor, $"Could not load sales stats: {popular.Error}");
            return;
        }

        DrawPopularTable(popular.Value ?? new List<PopularRow>());
    }

    private void DrawPopularControls()
    {
        var source = (int)popularSource;
        if (ImGui.RadioButton("Trending now", ref source, (int)PopularSource.Trending))
            popularSource = PopularSource.Trending;
        ImGui.SameLine();
        if (ImGui.RadioButton("By category", ref source, (int)PopularSource.Category))
            popularSource = PopularSource.Category;

        if (popularSource == PopularSource.Category && plugin.MarketCategories.Count > 0)
        {
            ImGui.SameLine();
            ImGui.SetNextItemWidth(200 * ImGuiHelpers.GlobalScale);
            var names = plugin.MarketCategories.Select(c => c.Name).ToArray();
            ImGui.Combo("##popularCategory", ref popularCategoryIndex, names, names.Length);
        }

        ImGui.SameLine();
        if (ImGui.Button("Refresh##popular"))
            popularRequest++;

        ImGui.SetNextItemWidth(130 * ImGuiHelpers.GlobalScale);
        var sort = (int)popularSort;
        var sortNames = new[] { "Gil traded per day", "Sold per day", "Average price" };
        if (ImGui.Combo("Sort##popularSort", ref sort, sortNames, sortNames.Length))
            popularSort = (PopularSort)sort;

        ImGui.SameLine();
        ImGui.SetNextItemWidth(80 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Min sold/day", ref popularMinSold, 0))
            popularMinSold = Math.Max(0, popularMinSold);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        if (ImGui.InputInt("Min price", ref popularMinPrice, 0))
            popularMinPrice = Math.Max(0, popularMinPrice);

        ImGui.SetNextItemWidth(-1);
        ImGui.InputTextWithHint("##popularFilter", "Filter by name...", ref popularFilter, 60);
    }

    // Combines NQ and HQ stats into one row; null when the item has no recent sales at all.
    private PopularRow? ToRow(AggregatedItem aggregated)
    {
        if (!plugin.MarketItems.TryGetValue(aggregated.ItemId, out var item))
            return null;

        var nqSold = aggregated.Nq?.SoldPerDay ?? 0;
        var hqSold = aggregated.Hq?.SoldPerDay ?? 0;
        var sold = nqSold + hqSold;
        if (sold <= 0)
            return null;

        var gil = nqSold * (aggregated.Nq?.AveragePrice ?? 0) + hqSold * (aggregated.Hq?.AveragePrice ?? 0);
        var cheapest = new[] { aggregated.Nq?.MinListing?.Value?.Price, aggregated.Hq?.MinListing?.Value?.Price }
            .Where(p => p is > 0)
            .Min();

        return new PopularRow(item, sold, gil / sold, gil, cheapest, hqSold / sold);
    }

    private void DrawPopularTable(List<PopularRow> rows)
    {
        var filter = popularFilter.Trim();
        var shown = rows
            .Where(r => r.SoldPerDay >= popularMinSold && r.AveragePrice >= popularMinPrice)
            .Where(r => filter.Length == 0 || r.Item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase));

        shown = popularSort switch
        {
            PopularSort.SoldPerDay => shown.OrderByDescending(r => r.SoldPerDay),
            PopularSort.AveragePrice => shown.OrderByDescending(r => r.AveragePrice),
            _ => shown.OrderByDescending(r => r.GilPerDay),
        };

        var list = shown.Take(MaxPopularRows).ToList();
        if (list.Count == 0)
        {
            ImGui.Text(rows.Count == 0 ? "No recent sales found." : "Nothing matches your filters.");
            return;
        }

        using var table = ImRaii.Table("##popular", 7, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("#", ImGuiTableColumnFlags.WidthFixed, 28 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Sold/day", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Avg price", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Gil/day", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Cheapest", ImGuiTableColumnFlags.WidthFixed, 95 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##actions", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < list.Count; i++)
        {
            var row = list[i];

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextDisabled($"{i + 1}");

            ImGui.TableNextColumn();
            DrawItemIcon(row.Item, 20);
            ImGui.SameLine();
            if (ImGui.Selectable($"{row.Item.Name}##popular{row.Item.Id}"))
                ShowItem(row.Item);
            if (row.Item.CanBeHq && ImGui.IsItemHovered())
                ImGui.SetTooltip($"{row.HqShare:P0} of sales are HQ");

            ImGui.TableNextColumn();
            ImGui.Text(row.SoldPerDay >= 10 ? $"{row.SoldPerDay:N0}" : $"{row.SoldPerDay:0.#}");
            ImGui.TableNextColumn();
            ImGui.Text(FormatGil(row.AveragePrice));
            ImGui.TableNextColumn();
            ImGui.Text(FormatGil(row.GilPerDay));
            ImGui.TableNextColumn();
            ImGui.Text(row.Cheapest.HasValue ? FormatGil(row.Cheapest.Value) : "-");

            ImGui.TableNextColumn();
            var watched = plugin.Watchlist.Contains(row.Item.Id);
            using (ImRaii.Disabled(watched))
            {
                if (ImGui.SmallButton(watched ? $"Watching##w{row.Item.Id}" : $"+ Watch##w{row.Item.Id}"))
                    plugin.Watchlist.Add(row.Item.Id);
            }
        }
    }
}
