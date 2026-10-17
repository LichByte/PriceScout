using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.ContextMenu;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Excel.Sheets;
using PriceScout.Windows;

namespace PriceScout;

public sealed record MarketItem(uint Id, string Name, uint IconId, bool CanBeHq, uint CategoryId);

public sealed record MarketCategory(uint Id, string Name);

public sealed record PlayerLocation(uint WorldId, string World, string? HomeWorld, string DataCenter, string? Region);

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IContextMenu ContextMenu { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static INotificationManager NotificationManager { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;

    private const string CommandName = "/pricescout";
    private const string ShortCommandName = "/psc";

    // Windows where right-clicking an item should offer "Check Prices", using the hovered item.
    // Chat log and crafting log keep their own context item and are handled separately.
    private static readonly HashSet<string> HoveredItemAddons = new()
    {
        "ItemSearch",
        "ItemSearchResult",
        "RecipeTree",
        "RecipeMaterialList",
        "RecipeProductList",
        "GatheringNote",
        "ContentsInfoDetail",
        "Shop",
        "ShopExchangeItem",
        "ShopExchangeCurrency",
        "InclusionShop",
        "ItemDetail",
    };

    public Configuration Configuration { get; }
    public UniversalisClient Universalis { get; } = new();
    public Watchlist Watchlist { get; }
    public SalesTracker SalesTracker { get; }

    // Every item that can be sold on the market board, keyed by item id.
    public IReadOnlyDictionary<uint, MarketItem> MarketItems { get; }
    public IReadOnlyList<MarketCategory> MarketCategories { get; }

    public readonly WindowSystem WindowSystem = new("PriceScout");
    private MainWindow MainWindow { get; }
    private UndercutWindow UndercutWindow { get; }
    private TooltipPrices TooltipPrices { get; }

    // Raised on the framework thread when the player logs in or moves to another world.
    public event System.Action? LocationChanged;

    private Dictionary<string, string> dataCenterRegions = new(StringComparer.OrdinalIgnoreCase);
    private uint lastWorldId;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        MarketItems = DataManager.GetExcelSheet<Item>()
            .Where(item => item.ItemSearchCategory.RowId != 0)
            .Select(item => new MarketItem(item.RowId, item.Name.ExtractText(), item.Icon, item.CanBeHq, item.ItemSearchCategory.RowId))
            .Where(item => item.Name.Length > 0)
            .ToDictionary(item => item.Id);

        // Market board categories in the same order the game lists them.
        var usedCategories = MarketItems.Values.Select(i => i.CategoryId).ToHashSet();
        MarketCategories = DataManager.GetExcelSheet<ItemSearchCategory>()
            .Where(c => usedCategories.Contains(c.RowId) && !c.Name.IsEmpty)
            .OrderBy(c => c.Category)
            .ThenBy(c => c.Order)
            .Select(c => new MarketCategory(c.RowId, c.Name.ExtractText()))
            .ToList();

        Watchlist = new Watchlist(this);
        SalesTracker = new SalesTracker(this);
        MainWindow = new MainWindow(this);
        UndercutWindow = new UndercutWindow(this);
        TooltipPrices = new TooltipPrices(this);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(UndercutWindow);

        var helpMessage = "Check market prices. Usage: /pricescout [item name]";
        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand) { HelpMessage = helpMessage });
        CommandManager.AddHandler(ShortCommandName, new CommandInfo(OnCommand) { HelpMessage = "Alias for /pricescout" });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw += TooltipPrices.Draw;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        ContextMenu.OnMenuOpened += OnContextMenuOpened;
        Framework.Update += OnFrameworkUpdate;

        _ = LoadDataCenterRegions();
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkUpdate;
        ContextMenu.OnMenuOpened -= OnContextMenuOpened;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.Draw -= TooltipPrices.Draw;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;

        WindowSystem.RemoveAllWindows();
        MainWindow.Dispose();
        UndercutWindow.Dispose();
        TooltipPrices.Dispose();
        Watchlist.Dispose();
        SalesTracker.Dispose();
        Universalis.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(ShortCommandName);
    }

    // Where the player is right now; null when not logged in.
    public PlayerLocation? CurrentLocation
    {
        get
        {
            if (!PlayerState.IsLoaded || !PlayerState.CurrentWorld.IsValid)
                return null;

            var world = PlayerState.CurrentWorld.Value;
            var dataCenter = world.DataCenter.Value.Name.ExtractText();
            var homeWorld = PlayerState.HomeWorld.IsValid ? PlayerState.HomeWorld.Value.Name.ExtractText() : null;
            return new PlayerLocation(world.RowId, world.Name.ExtractText(), homeWorld, dataCenter, RegionFor(dataCenter, world.DataCenter.Value.Region.RowId));
        }
    }

    // Resolves the Universalis scope (data center or region name) from settings or the player's current world.
    public string? ResolveScope()
    {
        if (!string.IsNullOrWhiteSpace(Configuration.ScopeOverride))
            return Configuration.ScopeOverride.Trim();

        var location = CurrentLocation;
        if (location == null)
            return null;

        return Configuration.Scope == SearchScope.Region ? location.Region : location.DataCenter;
    }

    private string? RegionFor(string dataCenter, uint regionRowId)
    {
        // Prefer Universalis' own data center list; fall back to the game's region ids if it hasn't loaded.
        if (dataCenterRegions.TryGetValue(dataCenter, out var region))
            return region;

        return regionRowId switch
        {
            1 => "Japan",
            2 => "North-America",
            3 => "Europe",
            4 => "Oceania",
            _ => null,
        };
    }

    private async Task LoadDataCenterRegions()
    {
        try
        {
            dataCenterRegions = await Universalis.GetDataCenterRegionsAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not load data center list from Universalis; using built-in region names");
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var worldId = PlayerState.IsLoaded && PlayerState.CurrentWorld.IsValid ? PlayerState.CurrentWorld.RowId : 0;
        if (worldId == lastWorldId)
            return;

        lastWorldId = worldId;
        if (worldId != 0)
            LocationChanged?.Invoke();
    }

    public static string WorldName(uint worldId) =>
        DataManager.GetExcelSheet<World>().TryGetRow(worldId, out var world) ? world.Name.ExtractText() : $"World {worldId}";

    public MarketItem? FindItemByName(string name)
    {
        var query = name.Trim();
        if (query.Length == 0)
            return null;

        return MarketItems.Values.FirstOrDefault(i => i.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? MarketItems.Values
                .Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(i => i.Name.Length)
                .FirstOrDefault();
    }

    public void ShowItem(MarketItem item) => MainWindow.ShowItem(item);

    private void OnCommand(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            MainWindow.Toggle();
            return;
        }

        var item = FindItemByName(args);
        if (item == null)
        {
            ChatGui.PrintError($"[PriceScout] No marketable item matches \"{args.Trim()}\".");
            return;
        }

        MainWindow.ShowItem(item);
    }

    private void OnContextMenuOpened(IMenuOpenedArgs args)
    {
        var itemId = GetContextItemId(args);
        if (itemId == 0 || !MarketItems.TryGetValue(itemId, out var item))
            return;

        args.AddMenuItem(new MenuItem
        {
            Name = "Check Prices",
            PrefixChar = 'P',
            OnClicked = _ => MainWindow.ShowItem(item),
        });
    }

    private static unsafe uint GetContextItemId(IMenuOpenedArgs args)
    {
        if (args.Target is MenuTargetInventory { TargetItem: { } inventoryItem })
            return inventoryItem.BaseItemId;

        if (args.MenuType != ContextMenuType.Default)
            return 0;

        ulong rawId;
        switch (args.AddonName)
        {
            case "ChatLog":
                var chatLog = AgentChatLog.Instance();
                rawId = chatLog == null ? 0 : chatLog->ContextItemId;
                break;
            case "RecipeNote":
                var recipeNote = AgentRecipeNote.Instance();
                rawId = recipeNote == null ? 0 : recipeNote->ContextMenuResultItemId;
                break;
            case { } name when HoveredItemAddons.Contains(name):
                rawId = GameGui.HoveredItem;
                break;
            default:
                return 0;
        }

        // Strip the HQ (1,000,000) and collectable (500,000) offsets to get the base item id.
        return (uint)(rawId % 500_000);
    }

    public void ToggleMainUi() => MainWindow.Toggle();

    private void OpenSettings() => MainWindow.OpenSettings();
}
