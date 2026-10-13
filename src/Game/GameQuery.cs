using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Read-only questions about the game. Every type and member here was checked against
/// sts2.dll v0.107.1 metadata.
/// </summary>
internal static class GameQuery
{
    public static List<ulong> SeatOrder()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState();
            return state?.Players.Select(p => p.NetId).ToList() ?? new List<ulong>();
        }
        catch { return new List<ulong>(); }
    }

    public static bool InCombat()
    {
        var cm = CombatManager.Instance;
        return cm is not null && cm.IsInProgress && !cm.IsOverOrEnding;
    }

    public static RoomKind Room()
    {
        if (InCombat()) return RoomKind.Combat;
        return RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom switch
        {
            MerchantRoom => RoomKind.Merchant,
            RestSiteRoom => RoomKind.RestSite,
            EventRoom => RoomKind.Event,
            TreasureRoom => RoomKind.Treasure,
            _ => RoomKind.Other,
        };
    }

    public static Node? TopOverlay() => NOverlayStack.Instance?.Peek() as Node;

    public static Node? ShopInventoryNode()
    {
        var inv = NMerchantRoom.Instance?.Inventory;
        return inv is { IsOpen: true } ? inv : null;
    }

    /// <summary>
    /// What's on top. <paramref name="isUpgradeGrid"/> marks the Smith screen
    /// (NDeckUpgradeSelectScreen), which only opens after the owner chose Smith.
    /// </summary>
    public static ScreenKind Classify(Node? overlay, out Node? screen, out bool isUpgradeGrid)
    {
        screen = overlay;
        isUpgradeGrid = false;
        switch (overlay)
        {
            case NCardRewardSelectionScreen:
                return ScreenKind.CardReward;
            case NChooseACardSelectionScreen:          // Attack/Skill/Power potions, Toolbox, Discovery-style cards
                return ScreenKind.GeneratedCardChoice;
            case NDeckUpgradeSelectScreen:
                isUpgradeGrid = true;
                return ScreenKind.DeckGrid;
            case NCardGridSelectionScreen:             // removal, transform, enchant, combat-pile picks
                return ScreenKind.DeckGrid;
            case not null:
                return ScreenKind.Other;
        }

        screen = ShopInventoryNode();
        return screen is not null ? ScreenKind.ShopInventory : ScreenKind.None;
    }

    public static Player? Me()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        return state is null ? null : LocalContext.GetMe(state.Players);
    }

    public static int MyGold() => Me()?.Gold ?? 0;

    public static List<CardModel> MyDeck() => Me()?.Deck?.Cards.ToList() ?? new List<CardModel>();

    public static List<string> MyRelics() =>
        Me()?.Relics.Select(r => Text(() => r.Title.GetFormattedText())).Where(s => s.Length > 0).ToList()
        ?? new List<string>();

    /// <param name="withCardData">Include the game's serialized card so the picker can show the real card.</param>
    public static CardView View(CardModel? card, bool withCardData = false)
    {
        if (card is null) return new CardView("", "?", "", 0);
        return new CardView(
            card.GetType().FullName ?? card.GetType().Name,
            Text(() => card.Title),
            Text(() => card.Description.GetFormattedText(), () => card.Description.GetRawText()),
            card.CurrentUpgradeLevel,
            withCardData ? CardCodec.Serialize(card) : "");
    }

    public static Player? PlayerById(ulong netId)
    {
        try { return RunManager.Instance?.DebugOnlyGetState()?.Players.FirstOrDefault(p => p.NetId == netId); }
        catch { return null; }
    }

    public static List<CardView> MyDeckViews() => MyDeck().Select(c => View(c)).ToList();

    /// <summary>The local player's merchant stock, as shown in the open shop.</summary>
    public static MerchantInventory? MyInventory()
    {
        var shown = NMerchantRoom.Instance?.Inventory?.Inventory;
        if (shown is not null) return shown;
        return RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is MerchantRoom room ? room.GetLocalInventory() : null;
    }

    /// <summary>Stocked shop entries in a stable order; the view's Index is the position in this list.</summary>
    public static List<(ShopItemView View, MerchantEntry Entry)> ShopEntries(out MerchantInventory? inventory)
    {
        var result = new List<(ShopItemView, MerchantEntry)>();
        inventory = MyInventory();
        if (inventory is null) return result;

        void Add(IEnumerable<MerchantEntry> entries, ShopItemType type)
        {
            foreach (var e in entries)
                if (e.IsStocked)
                    result.Add((Describe(e, type, result.Count), e));
        }

        Add(inventory.CharacterCardEntries, ShopItemType.Card);
        Add(inventory.ColorlessCardEntries, ShopItemType.ColorlessCard);
        Add(inventory.RelicEntries, ShopItemType.Relic);
        Add(inventory.PotionEntries, ShopItemType.Potion);
        if (inventory.CardRemovalEntry is { IsStocked: true } removal)
            result.Add((Describe(removal, ShopItemType.CardRemoval, result.Count), removal));
        return result;
    }

    private static ShopItemView Describe(MerchantEntry e, ShopItemType type, int index)
    {
        string id = "", title = "Remove a card", desc = "";
        switch (e)
        {
            case MerchantCardEntry card:
                var v = View(card.CreationResult?.Card);
                id = v.Id; title = v.Title; desc = v.Description;
                break;
            case MerchantRelicEntry { Model: { } relic }:
                id = relic.GetType().FullName ?? "";
                title = Text(() => relic.Title.GetFormattedText());
                desc = Text(() => relic.DynamicDescription.GetFormattedText());
                break;
            case MerchantPotionEntry { Model: { } potion }:
                id = potion.GetType().FullName ?? "";
                title = Text(() => potion.Title.GetFormattedText());
                desc = Text(() => potion.DynamicDescription.GetFormattedText());
                break;
        }
        return new ShopItemView(index, type, id, title, desc, e.Cost, e.EnoughGold);
    }

    /// <summary>Buys through the merchant exactly as clicking the slot would.</summary>
    public static bool Purchase(MerchantEntry entry, MerchantInventory inventory, string label)
    {
        try
        {
            var task = entry.OnTryPurchaseWrapper(inventory);
            task.ContinueWith(t =>
            {
                if (t.IsFaulted) Log.Error($"Purchase of {label} threw", t.Exception);
                else if (!t.Result) Log.Warn($"Purchase of {label} was refused by the game");
            });
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Purchase of {label} failed", ex);
            return false;
        }
    }

    /// <summary>Display text, with BBCode stripped; tries each source until one yields text.</summary>
    private static string Text(params Func<string?>[] sources)
    {
        foreach (var source in sources)
        {
            try
            {
                var s = source();
                if (!string.IsNullOrEmpty(s))
                    return System.Text.RegularExpressions.Regex.Replace(s, @"\[/?[a-zA-Z_]+(=[^\]]*)?\]", string.Empty);
            }
            catch { /* try the next source */ }
        }
        return string.Empty;
    }
}

/// <summary>Performs choices on the owner's own screens, the same way a click would, so vanilla sync carries them.</summary>
internal static class UiDriver
{
    public static bool IsGrid(Node screen) => screen is NCardGridSelectionScreen;

    public static List<NCardHolder> Holders(Node screen) =>
        IsGrid(screen)
            ? Nodes.FindAll<NGridCardHolder>(screen).Cast<NCardHolder>().ToList()
            : Nodes.FindAll<NCardHolder>(screen);

    public static List<CardView> Options(Node screen) =>
        Holders(screen).Select(h => GameQuery.View(h.CardModel, withCardData: true)).ToList();

    /// <summary>Some selection screens finish setting up a frame or two after they appear.</summary>
    public static bool IsReady(Node screen)
    {
        for (var t = screen.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField("_completionSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f is not null) return f.GetValue(screen) is not null;
        }
        return Holders(screen).Count > 0;
    }

    public static bool HasSkip(Node screen) => SkipButton(screen) is not null;

    public static bool PressHolder(Node screen, int index)
    {
        var holders = Holders(screen);
        if (index < 0 || index >= holders.Count) return false;
        var holder = holders[index];
        var grid = IsGrid(screen) ? Nodes.FindFirst<NCardGrid>(screen) : null;
        if (grid is not null) grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);
        else holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
        return true;
    }

    public static bool PressSkip(Node screen)
    {
        var skip = SkipButton(screen);
        if (skip is null) return false;
        skip.ForceClick();
        return true;
    }

    public static bool ClickConfirmIfEnabled(Node screen)
    {
        var btn = Nodes.FindAll<NConfirmButton>(screen).FirstOrDefault(b => b.IsEnabled && b.IsVisibleInTree());
        if (btn is null) return false;
        btn.ForceClick();
        return true;
    }

    /// <summary>Card rewards: the first alternative button is Skip (Reroll comes second when present). Choose-a-card: its skip button, when the choice allows one.</summary>
    private static NClickableControl? SkipButton(Node screen)
    {
        var alt = Nodes.FindAll<NCardRewardAlternativeButton>(screen).FirstOrDefault(b => b.IsVisibleInTree());
        if (alt is not null) return alt;
        var skip = Nodes.FindFirst<NChoiceSelectionSkipButton>(screen);
        return skip is not null && skip.IsVisibleInTree() && skip.IsEnabled ? skip : null;
    }
}
