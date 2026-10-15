using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace PriceScout;

public sealed class Listing
{
    [JsonPropertyName("pricePerUnit")] public long PricePerUnit { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("hq")] public bool Hq { get; set; }
    [JsonPropertyName("worldName")] public string? WorldName { get; set; }
    [JsonPropertyName("worldID")] public uint? WorldId { get; set; }
    [JsonPropertyName("retainerName")] public string? RetainerName { get; set; }
    [JsonPropertyName("lastReviewTime")] public long LastReviewTime { get; set; }
}

public sealed class Sale
{
    [JsonPropertyName("pricePerUnit")] public long PricePerUnit { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("hq")] public bool Hq { get; set; }
    [JsonPropertyName("worldName")] public string? WorldName { get; set; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; set; }
    [JsonPropertyName("buyerName")] public string? BuyerName { get; set; }
}

public sealed class MarketData
{
    [JsonPropertyName("itemID")] public uint ItemId { get; set; }
    [JsonPropertyName("listings")] public List<Listing> Listings { get; set; } = new();
    [JsonPropertyName("recentHistory")] public List<Sale> RecentHistory { get; set; } = new();
    [JsonPropertyName("averagePriceNQ")] public double AveragePriceNq { get; set; }
    [JsonPropertyName("averagePriceHQ")] public double AveragePriceHq { get; set; }
    [JsonPropertyName("regularSaleVelocity")] public double SaleVelocity { get; set; }
    [JsonPropertyName("lastUploadTime")] public long LastUploadTime { get; set; }

    // World id (as a string) -> unix ms of the last market board upload for that world.
    // Only present for data center and region queries.
    [JsonPropertyName("worldUploadTimes")] public Dictionary<string, long>? WorldUploadTimes { get; set; }

    [JsonIgnore] public DateTime FetchedAt { get; set; }
}

// The aggregated endpoint reports each value at world, data center and region level;
// only the levels covered by the requested scope are filled in.
public sealed class Scoped<T> where T : class
{
    [JsonPropertyName("world")] public T? World { get; set; }
    [JsonPropertyName("dc")] public T? Dc { get; set; }
    [JsonPropertyName("region")] public T? Region { get; set; }

    // The narrowest level available, which matches the scope that was asked for.
    [JsonIgnore] public T? Value => World ?? Dc ?? Region;
}

public sealed class PriceValue
{
    [JsonPropertyName("price")] public double Price { get; set; }
    [JsonPropertyName("worldId")] public uint? WorldId { get; set; }
}

public sealed class QuantityValue
{
    [JsonPropertyName("quantity")] public double Quantity { get; set; }
}

public sealed class AggregatedQuality
{
    [JsonPropertyName("minListing")] public Scoped<PriceValue>? MinListing { get; set; }
    [JsonPropertyName("averageSalePrice")] public Scoped<PriceValue>? AverageSalePrice { get; set; }
    [JsonPropertyName("dailySaleVelocity")] public Scoped<QuantityValue>? DailySaleVelocity { get; set; }

    [JsonIgnore] public double SoldPerDay => DailySaleVelocity?.Value?.Quantity ?? 0;
    [JsonIgnore] public double AveragePrice => AverageSalePrice?.Value?.Price ?? 0;
}

public sealed class AggregatedItem
{
    [JsonPropertyName("itemId")] public uint ItemId { get; set; }
    [JsonPropertyName("nq")] public AggregatedQuality? Nq { get; set; }
    [JsonPropertyName("hq")] public AggregatedQuality? Hq { get; set; }
}

public sealed record WorldPrice(double? CheapestNq, double? CheapestHq);

public sealed class UniversalisClient : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(2);

    private readonly HttpClient http = new()
    {
        BaseAddress = new Uri("https://universalis.app/api/v2/"),
        Timeout = TimeSpan.FromSeconds(15),
    };

    private static readonly TimeSpan HistoryCacheLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, MarketData> cache = new();
    private readonly ConcurrentDictionary<string, (List<Sale> Sales, DateTime FetchedAt)> historyCache = new();

    public UniversalisClient()
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd("PriceScout-Dalamud/0.1");
    }

    // Universalis sometimes answers 502/503/504 when it's busy; those usually succeed on a retry.
    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken token)
    {
        var delays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await http.GetFromJsonAsync<T>(url, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < delays.Length && IsTransient(ex, token))
            {
                await Task.Delay(delays[attempt], token).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(Exception ex, CancellationToken token) => ex switch
    {
        HttpRequestException { StatusCode: null } => true,
        HttpRequestException { StatusCode: { } status } => (int)status >= 500 || (int)status == 429,
        // HttpClient's own timeout surfaces as a cancellation that we didn't ask for.
        TaskCanceledException => !token.IsCancellationRequested,
        _ => false,
    };

    // A short, readable explanation of a failed request for showing in the UI.
    public static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } when (int)status >= 500 =>
            "Universalis is busy and didn't answer in time. Try again in a moment, or switch to \"My data center\" (region-wide searches are the slowest).",
        HttpRequestException { StatusCode: { } status } when (int)status == 404 =>
            "Universalis doesn't recognise that data center, world or region name.",
        HttpRequestException { StatusCode: { } status } when (int)status == 429 =>
            "Too many requests to Universalis. Wait a few seconds and try again.",
        HttpRequestException { StatusCode: null } => "Couldn't reach Universalis. Check your internet connection.",
        TaskCanceledException => "Universalis took too long to answer. Try again in a moment.",
        _ => ex.Message,
    };

    // Current listings plus sale stats. Recent sale entries are deliberately not requested here:
    // they're the slow part of the API (especially region-wide), so they're loaded separately.
    public async Task<MarketData?> GetLookupAsync(string scope, uint itemId, int listings, CancellationToken token)
    {
        var key = $"lookup:{scope}:{itemId}:{listings}";
        if (cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLifetime)
            return cached;

        var listingsTask = GetJsonAsync<MarketData>($"{Uri.EscapeDataString(scope)}/{itemId}?listings={listings}&entries=0", token);
        var statsTask = GetAggregatedAsync(scope, new[] { itemId }, token);

        var data = await listingsTask.ConfigureAwait(false);
        if (data == null)
            return null;

        try
        {
            var stats = (await statsTask.ConfigureAwait(false)).FirstOrDefault();
            if (stats != null)
            {
                data.AveragePriceNq = stats.Nq?.AveragePrice ?? 0;
                data.AveragePriceHq = stats.Hq?.AveragePrice ?? 0;
                data.SaleVelocity = (stats.Nq?.SoldPerDay ?? 0) + (stats.Hq?.SoldPerDay ?? 0);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            // Listings are still useful without the averages.
            Plugin.Log.Debug(ex, $"Sale stats unavailable for item {itemId}");
        }

        data.FetchedAt = DateTime.UtcNow;
        cache[key] = data;
        return data;
    }

    // The cheapest NQ and HQ listing on each world. A data center or region query only returns the overall
    // cheapest listings, which can all come from a few worlds, so each world is asked about separately.
    public async Task<Dictionary<uint, WorldPrice>> GetCheapestByWorldAsync(IEnumerable<uint> worldIds, uint itemId, CancellationToken token)
    {
        var results = new ConcurrentDictionary<uint, WorldPrice>();
        using var limiter = new SemaphoreSlim(6);

        var tasks = worldIds.Distinct().Select(async worldId =>
        {
            await limiter.WaitAsync(token).ConfigureAwait(false);
            try
            {
                var stats = (await GetAggregatedAsync(worldId.ToString(), new[] { itemId }, token).ConfigureAwait(false)).FirstOrDefault();
                results[worldId] = new WorldPrice(
                    stats?.Nq?.MinListing?.World?.Price is > 0 and var nq ? nq : null,
                    stats?.Hq?.MinListing?.World?.Price is > 0 and var hq ? hq : null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
            {
                // Leave this world out; the table falls back to the listings it already has.
                Plugin.Log.Debug(ex, $"Per-world price failed for world {worldId}");
            }
            finally
            {
                limiter.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new Dictionary<uint, WorldPrice>(results);
    }

    // The most recent sales, newest first.
    public async Task<List<Sale>> GetRecentSalesAsync(string scope, uint itemId, int count, CancellationToken token)
    {
        var key = $"recent:{scope}:{itemId}:{count}";
        if (historyCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLifetime)
            return cached.Sales;

        var data = await GetJsonAsync<HistoryData>($"history/{Uri.EscapeDataString(scope)}/{itemId}?entriesToReturn={count}", token).ConfigureAwait(false);
        var sales = data?.Entries ?? new List<Sale>();
        historyCache[key] = (sales, DateTime.UtcNow);
        return sales;
    }

    public async Task<MarketData?> GetMarketDataAsync(
        string scope, uint itemId, int listings, int history, CancellationToken token)
    {
        var key = CacheKey(scope, itemId, listings, history);
        if (cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLifetime)
            return cached;

        var url = $"{Uri.EscapeDataString(scope)}/{itemId}?listings={listings}&entries={history}";
        var data = await GetJsonAsync<MarketData>(url, token).ConfigureAwait(false);
        if (data == null)
            return null;

        data.FetchedAt = DateTime.UtcNow;
        cache[key] = data;
        return data;
    }

    private sealed class MultiMarketData
    {
        [JsonPropertyName("items")] public Dictionary<string, MarketData>? Items { get; set; }
    }

    private sealed class HistoryData
    {
        [JsonPropertyName("entries")] public List<Sale> Entries { get; set; } = new();
    }

    // Fetches several items at once. Universalis allows up to 100 ids per request.
    public async Task<Dictionary<uint, MarketData>> GetMarketDataBatchAsync(
        string scope, IEnumerable<uint> itemIds, int listings, int history, CancellationToken token)
    {
        var results = new Dictionary<uint, MarketData>();
        var missing = new List<uint>();
        foreach (var id in itemIds.Distinct())
        {
            if (cache.TryGetValue(CacheKey(scope, id, listings, history), out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheLifetime)
                results[id] = cached;
            else
                missing.Add(id);
        }

        foreach (var chunk in missing.Chunk(100))
        {
            if (chunk.Length == 1)
            {
                var single = await GetMarketDataAsync(scope, chunk[0], listings, history, token).ConfigureAwait(false);
                if (single != null)
                    results[chunk[0]] = single;
                continue;
            }

            var url = $"{Uri.EscapeDataString(scope)}/{string.Join(',', chunk)}?listings={listings}&entries={history}";
            var multi = await GetJsonAsync<MultiMarketData>(url, token).ConfigureAwait(false);
            foreach (var (key, data) in multi?.Items ?? new Dictionary<string, MarketData>())
            {
                if (!uint.TryParse(key, out var id))
                    continue;

                data.FetchedAt = DateTime.UtcNow;
                cache[CacheKey(scope, id, listings, history)] = data;
                results[id] = data;
            }
        }

        return results;
    }

    // Sales from the last `days` days, newest first.
    public async Task<List<Sale>> GetHistoryAsync(string scope, uint itemId, int days, CancellationToken token)
    {
        var key = $"history:{scope}:{itemId}:{days}";
        if (historyCache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < HistoryCacheLifetime)
            return cached.Sales;

        var url = $"history/{Uri.EscapeDataString(scope)}/{itemId}?entriesWithin={days * 86400}&entriesToReturn=999";
        var data = await GetJsonAsync<HistoryData>(url, token).ConfigureAwait(false);
        var sales = data?.Entries ?? new List<Sale>();
        historyCache[key] = (sales, DateTime.UtcNow);
        return sales;
    }

    private static string CacheKey(string scope, uint itemId, int listings, int history) => $"{scope}:{itemId}:{listings}:{history}";

    private sealed class RecentlyUpdatedResponse
    {
        [JsonPropertyName("items")] public List<RecentlyUpdatedItem> Items { get; set; } = new();
    }

    private sealed class RecentlyUpdatedItem
    {
        [JsonPropertyName("itemID")] public uint ItemId { get; set; }
    }

    private sealed class AggregatedResponse
    {
        [JsonPropertyName("results")] public List<AggregatedItem> Results { get; set; } = new();
    }

    // Item ids whose market data players uploaded most recently on a data center - a good signal of what's being traded.
    public async Task<List<uint>> GetRecentlyUpdatedItemsAsync(string dataCenter, int count, CancellationToken token)
    {
        var url = $"extra/stats/most-recently-updated?dcName={Uri.EscapeDataString(dataCenter)}&entries={Math.Clamp(count, 1, 200)}";
        var response = await GetJsonAsync<RecentlyUpdatedResponse>(url, token).ConfigureAwait(false);
        return response?.Items.Select(i => i.ItemId).Distinct().ToList() ?? new List<uint>();
    }

    // Sale velocity, average price and cheapest listing for many items, 100 per request.
    public async Task<List<AggregatedItem>> GetAggregatedAsync(string scope, IEnumerable<uint> itemIds, CancellationToken token)
    {
        var results = new List<AggregatedItem>();
        foreach (var chunk in itemIds.Distinct().Chunk(100))
        {
            var url = $"aggregated/{Uri.EscapeDataString(scope)}/{string.Join(',', chunk)}";
            var response = await GetJsonAsync<AggregatedResponse>(url, token).ConfigureAwait(false);
            if (response != null)
                results.AddRange(response.Results);
        }
        return results;
    }

    private sealed class DataCenterInfo
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("region")] public string Region { get; set; } = string.Empty;
    }

    // Data center name -> the region name Universalis uses for it (e.g. "Crystal" -> "North-America").
    public async Task<Dictionary<string, string>> GetDataCenterRegionsAsync(CancellationToken token)
    {
        var dataCenters = await GetJsonAsync<List<DataCenterInfo>>("data-centers", token).ConfigureAwait(false);
        var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dc in dataCenters ?? new List<DataCenterInfo>())
            regions[dc.Name] = dc.Region;
        return regions;
    }

    public void Dispose() => http.Dispose();
}
