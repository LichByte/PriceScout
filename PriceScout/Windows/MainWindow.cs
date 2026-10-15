using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;

namespace PriceScout.Windows;

public partial class MainWindow : Window, IDisposable
{
    private const int MaxSuggestions = 15;

    // Data older than this is shown in a warning color.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);
    private static readonly Vector4 StaleColor = new(1f, 0.6f, 0.2f, 1f);
    private static readonly Vector4 ErrorColor = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 HereColor = new(0.45f, 0.85f, 1f, 1f);

    private readonly Plugin plugin;

    private string searchText = string.Empty;
    private List<MarketItem> suggestions = new();

    private MarketItem? selectedItem;
    private string? loadedScope;
    private MarketData? data;
    private string? error;
    private bool loading;
    private CancellationTokenSource? requestCts;

    // Loaded separately from listings because sale history is the slow part of the Universalis API.
    private readonly AsyncLoad<List<Sale>> recentSales = new();
    private readonly AsyncLoad<Dictionary<uint, WorldPrice>> worldPrices = new();

    // Set when ShowItem is called so the Lookup tab gets focus on the next draw.
    private bool focusLookupTab;

    public MainWindow(Plugin plugin)
        : base("PriceScout##PriceScoutMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(560, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };

        this.plugin = plugin;
        plugin.LocationChanged += OnLocationChanged;
    }

    public void Dispose()
    {
        plugin.LocationChanged -= OnLocationChanged;
        ingredientPrices.Dispose();
        priceHistory.Dispose();
        shoppingPlan.Dispose();
        popular.Dispose();
        recentSales.Dispose();
        worldPrices.Dispose();
        requestCts?.Cancel();
        requestCts?.Dispose();
    }

    public void ShowItem(MarketItem item)
    {
        IsOpen = true;
        focusLookupTab = true;
        searchText = item.Name;
        suggestions.Clear();
        selectedItem = item;
        Fetch();
    }

    // Logging in or travelling to another world changes which prices are relevant, so reload them.
    private void OnLocationChanged()
    {
        if (!string.IsNullOrWhiteSpace(plugin.Configuration.ScopeOverride))
            return;

        Fetch();
        plugin.Watchlist.RefreshNow();
    }

    private void Fetch()
    {
        if (selectedItem == null)
            return;

        var scope = plugin.ResolveScope();
        if (scope == null)
        {
            error = "Log in to a character, or set a data center/region override below.";
            return;
        }

        requestCts?.Cancel();
        requestCts?.Dispose();
        requestCts = new CancellationTokenSource();
        var token = requestCts.Token;
        var item = selectedItem;
        var config = plugin.Configuration;

        loading = true;
        error = null;
        data = null;
        loadedScope = scope;

        Task.Run(async () =>
        {
            try
            {
                var result = await plugin.Universalis
                    .GetLookupAsync(scope, item.Id, config.ListingCount, token)
                    .ConfigureAwait(false);
                if (token.IsCancellationRequested)
                    return;

                data = result;
                error = result == null ? "Universalis returned no data for this item." : null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // A newer request replaced this one.
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Universalis request failed");
                error = UniversalisClient.Describe(ex);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                    loading = false;
            }
        }, token);
    }

    public override void Draw()
    {
        using var tabs = ImRaii.TabBar("##mainTabs");
        if (!tabs.Success)
            return;

        var lookupFlags = focusLookupTab ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        focusLookupTab = false;
        if (ImGui.BeginTabItem("Lookup", lookupFlags))
        {
            DrawLookup();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Popular"))
        {
            DrawPopular();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Watchlist ({plugin.Configuration.Watchlist.Count})###watchlist"))
        {
            DrawWatchlist();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem($"Shopping list ({plugin.Configuration.ShoppingList.Count})###shopping"))
        {
            DrawShoppingList();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Sales"))
        {
            DrawSales();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Settings"))
        {
            DrawSettings();
            ImGui.EndTabItem();
        }
    }

    private void DrawSettings()
    {
        var config = plugin.Configuration;

        var tooltips = config.ShowTooltipPrices;
        if (ImGui.Checkbox("Show prices next to item tooltips", ref tooltips))
        {
            config.ShowTooltipPrices = tooltips;
            config.Save();
        }

        var undercut = config.UndercutCheckerEnabled;
        if (ImGui.Checkbox("Open the undercut checker when I open a retainer's sell list", ref undercut))
        {
            config.UndercutCheckerEnabled = undercut;
            config.Save();
        }

        var alerts = config.PriceAlertsEnabled;
        if (ImGui.Checkbox("Watchlist price alerts", ref alerts))
        {
            config.PriceAlertsEnabled = alerts;
            config.Save();
        }

        var saleNotifications = config.SaleNotificationsEnabled;
        if (ImGui.Checkbox("Pop-up notification when a retainer sells something", ref saleNotifications))
        {
            config.SaleNotificationsEnabled = saleNotifications;
            config.Save();
        }
    }

    private void DrawLookup()
    {
        DrawSearch();
        DrawScopeSettings();
        ImGui.Separator();

        if (selectedItem == null)
        {
            ImGui.TextWrapped("Search for an item above, type /pricescout <item name>, or right-click an item (inventory, market board, chat links, crafting log) and choose \"Check Prices\".");
            return;
        }

        DrawItemHeader(selectedItem);

        if (loading)
        {
            ImGui.Text("Loading prices from Universalis...");
            return;
        }

        if (error != null)
        {
            ImGui.TextColored(ErrorColor, error);
            return;
        }

        if (data != null)
            DrawMarketData(data, selectedItem, plugin.CurrentLocation);
    }

    private void DrawSearch()
    {
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputTextWithHint("##search", "Search for an item...", ref searchText, 100))
        {
            var query = searchText.Trim();
            suggestions = query.Length < 2
                ? new List<MarketItem>()
                : plugin.MarketItems.Values
                    .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(i => !i.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(i => i.Name.Length)
                    .Take(MaxSuggestions)
                    .ToList();
        }

        if (suggestions.Count == 0)
            return;

        using var list = ImRaii.Child("##suggestions", new Vector2(-1, 150 * ImGuiHelpers.GlobalScale), true);
        if (!list.Success)
            return;

        foreach (var item in suggestions)
        {
            if (ImGui.Selectable($"{item.Name}##{item.Id}"))
            {
                ShowItem(item);
                break;
            }
        }
    }

    private void DrawScopeSettings()
    {
        var config = plugin.Configuration;

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Search:");
        ImGui.SameLine();

        var scope = (int)config.Scope;
        if (ImGui.RadioButton("My data center", ref scope, (int)SearchScope.DataCenter))
            SetScope(SearchScope.DataCenter);
        ImGui.SameLine();
        if (ImGui.RadioButton("My region", ref scope, (int)SearchScope.Region))
            SetScope(SearchScope.Region);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160 * ImGuiHelpers.GlobalScale);
        var scopeOverride = config.ScopeOverride;
        if (ImGui.InputTextWithHint("##override", "or type a DC/world", ref scopeOverride, 40))
        {
            config.ScopeOverride = scopeOverride;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Optional. Any data center, world or region name, e.g. Aether, Gilgamesh, Europe.\nLeave empty to use where you are.");
        if (ImGui.IsItemDeactivatedAfterEdit())
            Fetch();

        ImGui.SameLine();
        using (ImRaii.Disabled(selectedItem == null || loading))
        {
            if (ImGui.Button("Refresh"))
                Fetch();
        }

        DrawLocationLine();
    }

    private void DrawLocationLine()
    {
        var config = plugin.Configuration;
        if (!string.IsNullOrWhiteSpace(config.ScopeOverride))
        {
            ImGui.AlignTextToFramePadding();
            ImGui.TextColored(StaleColor, $"Showing {config.ScopeOverride.Trim()} instead of where you are.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Use my location"))
            {
                config.ScopeOverride = string.Empty;
                config.Save();
                Fetch();
                plugin.Watchlist.RefreshNow();
            }
            return;
        }

        var location = plugin.CurrentLocation;
        if (location == null)
        {
            ImGui.TextDisabled("Not logged in. Your world, data center and region are detected when you log in.");
            return;
        }

        var home = location.HomeWorld != null && location.HomeWorld != location.World ? $"   (home: {location.HomeWorld})" : string.Empty;
        ImGui.TextDisabled($"You're on {location.World}  -  {location.DataCenter}  -  {location.Region ?? "unknown region"}{home}");
    }

    private void SetScope(SearchScope scope)
    {
        plugin.Configuration.Scope = scope;
        plugin.Configuration.Save();
        Fetch();
        plugin.Watchlist.RefreshNow();
    }

    private void DrawItemHeader(MarketItem item)
    {
        DrawItemIcon(item, 32);
        ImGui.SameLine();

        using (ImRaii.Group())
        {
            ImGui.Text(item.Name);
            ImGui.TextDisabled(loadedScope != null ? $"Prices on {loadedScope}" : string.Empty);
        }

        var buttonWidth = 110 * ImGuiHelpers.GlobalScale;
        var spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SameLine(ImGui.GetContentRegionAvail().X + ImGui.GetCursorPosX() - buttonWidth * 2 - spacing);
        if (ImGui.Button("+ Shopping list##shop", new Vector2(buttonWidth, 0)))
            AddToShoppingList(item.Id, 1);

        ImGui.SameLine();
        var watched = plugin.Watchlist.Contains(item.Id);
        if (ImGui.Button(watched ? "Unwatch##watch" : "+ Watchlist##watch", new Vector2(buttonWidth, 0)))
        {
            if (watched)
                plugin.Watchlist.Remove(item.Id);
            else
                plugin.Watchlist.Add(item.Id);
        }
    }

    private static void DrawItemIcon(MarketItem item, float size)
    {
        var icon = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(item.IconId)).GetWrapOrEmpty();
        ImGui.Image(icon.Handle, new Vector2(size, size) * ImGuiHelpers.GlobalScale);
    }

    private void DrawMarketData(MarketData market, MarketItem item, PlayerLocation? location)
    {
        var averages = item.CanBeHq
            ? $"Avg sale NQ: {FormatGil(market.AveragePriceNq)}   HQ: {FormatGil(market.AveragePriceHq)}"
            : $"Avg sale: {FormatGil(market.AveragePriceNq)}";
        ImGui.Text($"{averages}   Sales/day: {market.SaleVelocity:0.#}");
        if (market.LastUploadTime > 0)
            ImGui.TextDisabled($"Newest data: {FormatAge(DateTimeOffset.FromUnixTimeMilliseconds(market.LastUploadTime))}");

        ImGui.Spacing();

        using var tabs = ImRaii.TabBar("##tabs");
        if (!tabs.Success)
            return;

        using (var tab = ImRaii.TabItem("Cheapest by world"))
        {
            if (tab.Success)
                DrawCheapestByWorld(market, item, location);
        }

        using (var tab = ImRaii.TabItem($"Listings ({market.Listings.Count})"))
        {
            if (tab.Success)
                DrawListings(market, item.CanBeHq);
        }

        using (var tab = ImRaii.TabItem("Recent sales"))
        {
            if (tab.Success)
                DrawHistory(item, item.CanBeHq);
        }

        using (var tab = ImRaii.TabItem("Price history"))
        {
            if (tab.Success)
                DrawPriceHistory(item);
        }

        if (Recipes.ForItem(item.Id).Count > 0)
        {
            using var tab = ImRaii.TabItem("Crafting profit");
            if (tab.Success)
                DrawCrafting(item, market);
        }
    }

    private sealed record WorldRow(string World, double? Nq, double? Hq, DateTimeOffset? Updated);

    private void DrawCheapestByWorld(MarketData market, MarketItem item, PlayerLocation? location)
    {
        var canBeHq = item.CanBeHq;
        var worldIds = (market.WorldUploadTimes ?? new Dictionary<string, long>()).Keys
            .Select(k => uint.TryParse(k, out var id) ? id : 0)
            .Where(id => id != 0)
            .ToList();

        // For a data center or region, ask each world for its own cheapest price.
        if (worldIds.Count > 1 && loadedScope != null)
        {
            worldPrices.Ensure($"{loadedScope}:{item.Id}:{market.FetchedAt.Ticks}", token =>
                plugin.Universalis.GetCheapestByWorldAsync(worldIds, item.Id, token));
        }

        var perWorld = worldIds.Count > 1 ? worldPrices.Value : null;
        if (worldIds.Count > 1 && worldPrices.Loading)
            ImGui.TextDisabled($"Checking all {worldIds.Count} worlds...");

        var rows = BuildWorldRows(market, perWorld);
        if (rows.Count == 0)
        {
            ImGui.Text("No listings right now.");
            return;
        }

        var columns = canBeHq ? 4 : 3;
        using var table = ImRaii.Table("##worlds", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("World");
        ImGui.TableSetupColumn(canBeHq ? "Cheapest NQ" : "Cheapest");
        if (canBeHq)
            ImGui.TableSetupColumn("Cheapest HQ");
        ImGui.TableSetupColumn("Updated");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var row in rows)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            if (row.World == location?.World)
                ImGui.TextColored(HereColor, $"{row.World} (you're here)");
            else if (row.World == location?.HomeWorld)
                ImGui.TextColored(HereColor, $"{row.World} (home)");
            else
                ImGui.Text(row.World);
            ImGui.TableNextColumn();
            ImGui.Text(row.Nq.HasValue ? FormatGil(row.Nq.Value) : "-");
            if (canBeHq)
            {
                ImGui.TableNextColumn();
                ImGui.Text(row.Hq.HasValue ? FormatGil(row.Hq.Value) : "-");
            }
            ImGui.TableNextColumn();
            DrawFreshness(row.Updated);
        }
    }

    // One row per world, cheapest first; worlds with nothing listed go last.
    // Uses each world's own cheapest price when available, otherwise the overall cheapest listings.
    private static List<WorldRow> BuildWorldRows(MarketData market, Dictionary<uint, WorldPrice>? perWorld)
    {
        var uploadTimes = new Dictionary<string, DateTimeOffset>();
        var worldNames = new Dictionary<uint, string>();
        foreach (var (worldId, ms) in market.WorldUploadTimes ?? new Dictionary<string, long>())
        {
            if (!uint.TryParse(worldId, out var id))
                continue;
            worldNames[id] = Plugin.WorldName(id);
            uploadTimes[worldNames[id]] = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        var fromListings = market.Listings
            .GroupBy(l => l.WorldName ?? "?")
            .ToDictionary(
                g => g.Key,
                g => new WorldPrice(
                    g.Where(l => !l.Hq).Select(l => (double?)l.PricePerUnit).Min(),
                    g.Where(l => l.Hq).Select(l => (double?)l.PricePerUnit).Min()));

        var names = uploadTimes.Keys.Union(fromListings.Keys);
        var rows = names.Select(name =>
        {
            var id = worldNames.FirstOrDefault(kv => kv.Value == name).Key;
            var price = perWorld != null && perWorld.TryGetValue(id, out var own) ? own : fromListings.GetValueOrDefault(name);
            return new WorldRow(name, price?.CheapestNq, price?.CheapestHq, uploadTimes.TryGetValue(name, out var t) ? t : null);
        });

        return rows
            .OrderBy(r => Math.Min(r.Nq ?? double.MaxValue, r.Hq ?? double.MaxValue))
            .ThenBy(r => r.World)
            .ToList();
    }

    private static void DrawFreshness(DateTimeOffset? updated)
    {
        if (updated == null)
        {
            ImGui.TextDisabled("unknown");
            return;
        }

        var text = FormatAge(updated.Value);
        if (DateTimeOffset.UtcNow - updated.Value > StaleAfter)
        {
            ImGui.TextColored(StaleColor, text);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Nobody has viewed this world's market board in a while, so these prices may be out of date.");
        }
        else
        {
            ImGui.Text(text);
        }
    }

    private static void DrawListings(MarketData market, bool canBeHq)
    {
        var columns = canBeHq ? 5 : 4;
        using var table = ImRaii.Table("##listings", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        if (canBeHq)
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Price");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("Total");
        ImGui.TableSetupColumn("World");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var l in market.Listings)
        {
            ImGui.TableNextRow();
            if (canBeHq)
            {
                ImGui.TableNextColumn();
                ImGui.Text(l.Hq ? "HQ" : string.Empty);
            }
            ImGui.TableNextColumn();
            ImGui.Text(FormatGil(l.PricePerUnit));
            ImGui.TableNextColumn();
            ImGui.Text(l.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(FormatGil(l.Total));
            ImGui.TableNextColumn();
            ImGui.Text(l.WorldName ?? "?");
        }
    }

    private void DrawHistory(MarketItem item, bool canBeHq)
    {
        var scope = loadedScope;
        if (scope == null)
            return;

        var count = plugin.Configuration.HistoryCount;
        recentSales.Ensure($"{scope}:{item.Id}:{count}", token =>
            plugin.Universalis.GetRecentSalesAsync(scope, item.Id, count, token));

        if (recentSales.Loading)
        {
            ImGui.Text("Loading recent sales...");
            return;
        }

        if (recentSales.Error != null)
        {
            ImGui.TextColored(ErrorColor, "Recent sales couldn't be loaded.");
            ImGui.TextWrapped(recentSales.Error);
            if (ImGui.Button("Try again##recentSales"))
                recentSales.Start($"{scope}:{item.Id}:{count}", token =>
                    plugin.Universalis.GetRecentSalesAsync(scope, item.Id, count, token));
            return;
        }

        var sales = recentSales.Value ?? new List<Sale>();
        if (sales.Count == 0)
        {
            ImGui.Text("No recent sales recorded.");
            return;
        }

        var columns = canBeHq ? 5 : 4;
        using var table = ImRaii.Table("##history", columns, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        if (canBeHq)
            ImGui.TableSetupColumn("HQ", ImGuiTableColumnFlags.WidthFixed, 30 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Price");
        ImGui.TableSetupColumn("Qty");
        ImGui.TableSetupColumn("World");
        ImGui.TableSetupColumn("When");
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        foreach (var s in sales)
        {
            ImGui.TableNextRow();
            if (canBeHq)
            {
                ImGui.TableNextColumn();
                ImGui.Text(s.Hq ? "HQ" : string.Empty);
            }
            ImGui.TableNextColumn();
            ImGui.Text(FormatGil(s.PricePerUnit));
            ImGui.TableNextColumn();
            ImGui.Text(s.Quantity.ToString());
            ImGui.TableNextColumn();
            ImGui.Text(s.WorldName ?? "?");
            ImGui.TableNextColumn();
            ImGui.Text(FormatAge(DateTimeOffset.FromUnixTimeSeconds(s.Timestamp)));
        }
    }

    private void DrawWatchlist()
    {
        var config = plugin.Configuration;
        var watchlist = plugin.Watchlist;

        ImGui.AlignTextToFramePadding();
        ImGui.Text("Refresh every");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        var minutes = config.WatchRefreshMinutes;
        if (ImGui.InputInt("min##refreshMinutes", ref minutes))
        {
            config.WatchRefreshMinutes = Math.Clamp(minutes, 1, 120);
            config.Save();
        }

        ImGui.SameLine();
        using (ImRaii.Disabled(watchlist.IsRefreshing))
        {
            if (ImGui.Button(watchlist.IsRefreshing ? "Refreshing..." : "Refresh now"))
                watchlist.RefreshNow();
        }

        ImGui.SameLine();
        var alerts = config.PriceAlertsEnabled;
        if (ImGui.Checkbox("Price alerts", ref alerts))
        {
            config.PriceAlertsEnabled = alerts;
            config.Save();
        }

        var scope = plugin.ResolveScope();
        ImGui.TextDisabled(scope != null ? $"Watching prices on {scope}" : "Log in to a character to load watchlist prices.");
        ImGui.Separator();

        if (config.Watchlist.Count == 0)
        {
            ImGui.TextWrapped("Your watchlist is empty. Look up an item and click \"+ Watchlist\" to track it here.");
            return;
        }

        using var table = ImRaii.Table("##watchlist", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Cheapest", ImGuiTableColumnFlags.WidthFixed, 110 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("World", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Checked", ImGuiTableColumnFlags.WidthFixed, 60 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Alert at or below", ImGuiTableColumnFlags.WidthFixed, 120 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("##remove", ImGuiTableColumnFlags.WidthFixed, 24 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        uint? toRemove = null;
        foreach (var watched in config.Watchlist)
        {
            if (!plugin.MarketItems.TryGetValue(watched.ItemId, out var item))
                continue;

            var result = watchlist.GetResult(watched.ItemId);

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            DrawItemIcon(item, 20);
            ImGui.SameLine();
            if (ImGui.Selectable($"{item.Name}##watch{item.Id}"))
                ShowItem(item);

            ImGui.TableNextColumn();
            if (result == null)
                ImGui.TextDisabled("loading...");
            else if (result.Error != null)
                ImGui.TextColored(ErrorColor, "error");
            else if (result.CheapestPrice <= 0)
                ImGui.TextDisabled("no listings");
            else
            {
                var hq = result.CheapestIsHq ? " HQ" : string.Empty;
                var belowAlert = watched.AlertBelow > 0 && result.CheapestPrice <= watched.AlertBelow;
                if (belowAlert)
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), $"{FormatGil(result.CheapestPrice)}{hq}");
                else
                    ImGui.Text($"{FormatGil(result.CheapestPrice)}{hq}");
            }
            if (result?.Error != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(result.Error);

            ImGui.TableNextColumn();
            ImGui.Text(result?.CheapestWorld ?? "-");

            ImGui.TableNextColumn();
            ImGui.Text(result != null ? FormatAge(result.FetchedAt) : "-");

            ImGui.TableNextColumn();
            ImGui.SetNextItemWidth(-1);
            var alertBelow = watched.AlertBelow;
            if (ImGui.InputInt($"##alert{item.Id}", ref alertBelow, 0))
            {
                watched.AlertBelow = Math.Max(0, alertBelow);
                config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Get a chat message and notification when the cheapest listing is at or below this price.\n0 = no alert.");

            ImGui.TableNextColumn();
            if (ImGui.SmallButton($"x##remove{item.Id}"))
                toRemove = item.Id;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Remove from watchlist");
        }

        if (toRemove.HasValue)
            watchlist.Remove(toRemove.Value);
    }

    private static string FormatGil(double amount) => amount <= 0 ? "-" : $"{amount:N0} gil";

    private static string FormatAge(DateTimeOffset time) => FormatAge(DateTime.UtcNow - time.UtcDateTime);

    private static string FormatAge(DateTime utcTime) => FormatAge(DateTime.UtcNow - utcTime);

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 1) return "just now";
        if (age.TotalHours < 1) return $"{(int)age.TotalMinutes}m ago";
        if (age.TotalDays < 1) return $"{(int)age.TotalHours}h ago";
        return $"{(int)age.TotalDays}d ago";
    }
}
