using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace PriceScout;

public sealed record OwnListing(uint ItemId, bool Hq, int Quantity, long Price);

public sealed record RetainerSummary(string Name, int ListedItems);

// Reads retainer data straight from game memory. Only meaningful while the game has it loaded
// (after opening the summoning bell, and for the active retainer while its sell list is open).
public static unsafe class RetainerReader
{
    public static string? ActiveRetainerName()
    {
        var retainers = RetainerManager.Instance();
        if (retainers == null)
            return null;

        var active = retainers->GetActiveRetainer();
        return active == null ? null : active->NameString;
    }

    // How many items the active retainer has listed, per the retainer data (not the sell list itself).
    public static int ActiveRetainerListedCount()
    {
        var retainers = RetainerManager.Instance();
        var active = retainers == null ? null : retainers->GetActiveRetainer();
        return active == null ? -1 : active->MarketItemCount;
    }

    public static List<RetainerSummary> AllRetainers()
    {
        var result = new List<RetainerSummary>();
        var retainers = RetainerManager.Instance();
        if (retainers == null)
            return result;

        for (uint i = 0; i < retainers->GetRetainerCount(); i++)
        {
            var retainer = retainers->GetRetainerBySortedIndex(i);
            if (retainer != null && retainer->Available)
                result.Add(new RetainerSummary(retainer->NameString, retainer->MarketItemCount));
        }

        return result;
    }

    public static List<OwnListing> ActiveSellList()
    {
        var found = new List<OwnListing>();
        var inventory = InventoryManager.Instance();
        var container = inventory == null ? null : inventory->GetInventoryContainer(InventoryType.RetainerMarket);
        if (container == null)
            return found;

        for (var slot = 0; slot < container->Size; slot++)
        {
            var item = container->GetInventorySlot(slot);
            if (item == null || item->ItemId == 0)
                continue;

            var hq = item->Flags.HasFlag(InventoryItem.ItemFlags.HighQuality);
            var price = inventory->GetRetainerMarketPrice((short)slot);
            found.Add(new OwnListing(item->ItemId, hq, (int)item->Quantity, (long)price));
        }

        return found;
    }
}
