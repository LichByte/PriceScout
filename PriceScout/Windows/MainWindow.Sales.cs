using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;

namespace PriceScout.Windows;

// "Sales" tab: everything your retainers have sold.
public partial class MainWindow
{
    private bool confirmClearSales;

    private void DrawSales()
    {
        var config = plugin.Configuration;
        var log = config.SaleLog;

        var now = DateTime.UtcNow;
        long Earned(TimeSpan window) => log
            .Where(s => s.Source != SaleSource.Unconfirmed && now - s.Time <= window)
            .Sum(s => s.Total ?? 0);

        ImGui.Text($"Today: {FormatGil(Earned(TimeSpan.FromDays(1)))}    7 days: {FormatGil(Earned(TimeSpan.FromDays(7)))}    30 days: {FormatGil(Earned(TimeSpan.FromDays(30)))}");
        ImGui.TextDisabled("Live sales are logged as they happen. Sales made while you were offline show up when you open that retainer's sell list.");

        if (log.Count == 0)
        {
            ImGui.Separator();
            ImGui.TextWrapped("No sales yet. To start tracking offline sales, open each retainer's sell list once so PriceScout knows what's listed.");
            return;
        }

        if (confirmClearSales)
        {
            if (ImGui.Button("Yes, clear the sales log"))
            {
                log.Clear();
                config.Save();
                confirmClearSales = false;
                return;
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                confirmClearSales = false;
        }
        else if (ImGui.Button("Clear log"))
        {
            confirmClearSales = true;
        }

        ImGui.Separator();

        using var table = ImRaii.Table("##sales", 6, ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH | ImGuiTableFlags.ScrollY);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("When", ImGuiTableColumnFlags.WidthFixed, 70 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Qty", ImGuiTableColumnFlags.WidthFixed, 40 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Gil", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Retainer", ImGuiTableColumnFlags.WidthFixed, 100 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 90 * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableHeadersRow();

        for (var i = 0; i < log.Count; i++)
        {
            var sale = log[i];
            var name = plugin.MarketItems.TryGetValue(sale.ItemId, out var item) ? item.Name : $"Item {sale.ItemId}";
            if (sale.Hq)
                name += " (HQ)";

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.Text(FormatAge(sale.Time));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(sale.Time.ToLocalTime().ToString("f"));

            ImGui.TableNextColumn();
            if (item != null && ImGui.Selectable($"{name}##sale{i}"))
                ShowItem(item);
            else if (item == null)
                ImGui.Text(name);

            ImGui.TableNextColumn();
            ImGui.Text(sale.Quantity?.ToString() ?? "?");

            ImGui.TableNextColumn();
            ImGui.Text(sale.Total.HasValue ? $"{sale.Total.Value:N0}" : "?");
            if (sale.Source == SaleSource.Live && ImGui.IsItemHovered())
                ImGui.SetTooltip("Amount from the game's sale message, after market fees.");

            ImGui.TableNextColumn();
            ImGui.Text(sale.Retainer ?? "-");

            ImGui.TableNextColumn();
            switch (sale.Source)
            {
                case SaleSource.Live:
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Sold");
                    break;
                case SaleSource.Confirmed:
                    ImGui.TextColored(new Vector4(0.4f, 1f, 0.4f, 1f), "Sold (offline)");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(sale.Buyer != null ? $"Bought by {sale.Buyer}" : "Matched in Universalis sale history");
                    break;
                default:
                    ImGui.TextColored(StaleColor, "Gone?");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("This listing disappeared while you were away, but no matching sale was found.\nIt probably sold, but it may have expired or been taken down.");
                    break;
            }
        }
    }
}
