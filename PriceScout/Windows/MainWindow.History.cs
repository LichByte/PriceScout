using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace PriceScout.Windows;

// "Price history" tab: a line chart of the average daily sale price.
public partial class MainWindow
{
    private static readonly int[] HistoryRanges = { 7, 14, 30 };
    private static readonly Vector4 NqLineColor = new(0.45f, 0.75f, 1f, 1f);
    private static readonly Vector4 HqLineColor = new(1f, 0.8f, 0.3f, 1f);

    private readonly AsyncLoad<List<Sale>> priceHistory = new();

    private sealed record DayPoint(DateTime Day, double? Nq, double? Hq, int Sold);

    private void DrawPriceHistory(MarketItem item)
    {
        var config = plugin.Configuration;
        var scope = loadedScope;
        if (scope == null)
            return;

        foreach (var days in HistoryRanges)
        {
            if (ImGui.RadioButton($"{days} days", config.HistoryDays == days))
            {
                config.HistoryDays = days;
                config.Save();
            }
            ImGui.SameLine();
        }
        ImGui.NewLine();

        var range = config.HistoryDays;
        priceHistory.Ensure($"{scope}:{item.Id}:{range}", token =>
            plugin.Universalis.GetHistoryAsync(scope, item.Id, range, token));

        if (priceHistory.Loading)
        {
            ImGui.Text("Loading sale history...");
            return;
        }

        if (priceHistory.Error != null)
        {
            ImGui.TextColored(ErrorColor, $"Could not load history: {priceHistory.Error}");
            return;
        }

        var sales = priceHistory.Value ?? new List<Sale>();
        if (sales.Count == 0)
        {
            ImGui.Text($"No sales recorded on {scope} in the last {range} days.");
            return;
        }

        var points = BuildDailyPoints(sales, range);
        DrawChart(points, item.CanBeHq);

        var total = sales.Sum(s => s.Quantity);
        ImGui.TextDisabled($"{sales.Count} sales ({total:N0} items) on {scope} in the last {range} days. Each point is that day's average price.");
    }

    private static List<DayPoint> BuildDailyPoints(List<Sale> sales, int range)
    {
        var today = DateTime.UtcNow.Date;
        var byDay = sales
            .GroupBy(s => DateTimeOffset.FromUnixTimeSeconds(s.Timestamp).UtcDateTime.Date)
            .ToDictionary(g => g.Key, g => g.ToList());

        var points = new List<DayPoint>();
        for (var d = range - 1; d >= 0; d--)
        {
            var day = today.AddDays(-d);
            if (!byDay.TryGetValue(day, out var daySales))
            {
                points.Add(new DayPoint(day, null, null, 0));
                continue;
            }

            points.Add(new DayPoint(day, WeightedAverage(daySales.Where(s => !s.Hq)), WeightedAverage(daySales.Where(s => s.Hq)), daySales.Sum(s => s.Quantity)));
        }

        return points;
    }

    private static double? WeightedAverage(IEnumerable<Sale> sales)
    {
        var list = sales.ToList();
        var quantity = list.Sum(s => s.Quantity);
        return quantity == 0 ? null : (double)list.Sum(s => s.PricePerUnit * s.Quantity) / quantity;
    }

    private static void DrawChart(List<DayPoint> points, bool canBeHq)
    {
        var values = points.SelectMany(p => new[] { p.Nq, canBeHq ? p.Hq : null }).Where(v => v.HasValue).Select(v => v!.Value).ToList();
        if (values.Count == 0)
            return;

        var min = values.Min();
        var max = values.Max();
        if (max - min < 1)
        {
            min -= 1;
            max += 1;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var labelWidth = 80 * scale;
        var size = new Vector2(ImGui.GetContentRegionAvail().X, 180 * scale);
        var origin = ImGui.GetCursorScreenPos();
        var plotMin = origin + new Vector2(labelWidth, 8 * scale);
        var plotMax = origin + size - new Vector2(8 * scale, 20 * scale);

        var drawList = ImGui.GetWindowDrawList();
        var gridColor = ImGui.GetColorU32(ImGuiCol.Border);
        var textColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);

        // Horizontal grid lines with price labels.
        for (var i = 0; i <= 4; i++)
        {
            var t = i / 4f;
            var y = plotMax.Y - (plotMax.Y - plotMin.Y) * t;
            drawList.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), gridColor);
            var price = min + (max - min) * t;
            drawList.AddText(new Vector2(origin.X, y - ImGui.GetTextLineHeight() / 2), textColor, $"{price:N0}");
        }

        float X(int index) => plotMin.X + (plotMax.X - plotMin.X) * (points.Count == 1 ? 0.5f : (float)index / (points.Count - 1));
        float Y(double price) => plotMax.Y - (plotMax.Y - plotMin.Y) * (float)((price - min) / (max - min));

        // Date labels at the start, middle and end.
        foreach (var index in new[] { 0, points.Count / 2, points.Count - 1 }.Distinct())
        {
            var label = points[index].Day.ToString("MMM d");
            var width = ImGui.CalcTextSize(label).X;
            drawList.AddText(new Vector2(X(index) - width / 2, plotMax.Y + 4 * scale), textColor, label);
        }

        DrawSeries(drawList, points, p => p.Nq, X, Y, ImGui.GetColorU32(NqLineColor));
        if (canBeHq)
            DrawSeries(drawList, points, p => p.Hq, X, Y, ImGui.GetColorU32(HqLineColor));

        ImGui.InvisibleButton("##chart", size);
        if (ImGui.IsItemHovered())
        {
            var mouseX = ImGui.GetIO().MousePos.X;
            var nearest = Enumerable.Range(0, points.Count).MinBy(i => Math.Abs(X(i) - mouseX));
            var point = points[nearest];
            drawList.AddLine(new Vector2(X(nearest), plotMin.Y), new Vector2(X(nearest), plotMax.Y), textColor);

            var lines = new List<string> { point.Day.ToString("ddd, MMM d") };
            lines.Add(point.Nq.HasValue ? $"{(canBeHq ? "NQ" : "Avg")}: {point.Nq.Value:N0} gil" : $"{(canBeHq ? "NQ" : "Avg")}: no sales");
            if (canBeHq)
                lines.Add(point.Hq.HasValue ? $"HQ: {point.Hq.Value:N0} gil" : "HQ: no sales");
            lines.Add($"{point.Sold} sold");
            ImGui.SetTooltip(string.Join("\n", lines));
        }

        if (canBeHq)
        {
            ImGui.TextColored(NqLineColor, "NQ");
            ImGui.SameLine();
            ImGui.TextColored(HqLineColor, "HQ");
        }
    }

    private static void DrawSeries(ImDrawListPtr drawList, List<DayPoint> points, Func<DayPoint, double?> value, Func<int, float> x, Func<double, float> y, uint color)
    {
        Vector2? previous = null;
        for (var i = 0; i < points.Count; i++)
        {
            var v = value(points[i]);
            if (!v.HasValue)
                continue;

            var current = new Vector2(x(i), y(v.Value));
            // Days without sales are skipped; the line connects the days around them.
            if (previous.HasValue)
                drawList.AddLine(previous.Value, current, color, 2f * ImGuiHelpers.GlobalScale);
            drawList.AddCircleFilled(current, 3f * ImGuiHelpers.GlobalScale, color);
            previous = current;
        }
    }
}
