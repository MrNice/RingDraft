using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rewards;
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
            EventRoom { CanonicalEvent: AncientEventModel } => RoomKind.Ancient,
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
    public static ScreenKind Classify(Node? overlay, out Node? screen, out bool isUpgradeGrid) =>
        Classify(overlay, out screen, out isUpgradeGrid, out _);

    /// <param name="grid">For deck grids: what choosing cards on it does (upgrade, remove, transform), when that can be told.</param>
    public static ScreenKind Classify(Node? overlay, out Node? screen, out bool isUpgradeGrid, out DeckGridKind grid)
    {
        screen = overlay;
        isUpgradeGrid = false;
        grid = DeckGridKind.Other;
        switch (overlay)
        {
            case NCardRewardSelectionScreen:
                return ScreenKind.CardReward;
            case NChooseACardSelectionScreen:          // Attack/Skill/Power potions, Toolbox, Discovery-style cards
                return ScreenKind.GeneratedCardChoice;
            case NChooseABundleSelectionScreen:        // Scroll Boxes: choose a pack
                return ScreenKind.Bundle;
            case NDeckUpgradeSelectScreen:
                isUpgradeGrid = true;
                grid = DeckGridKind.Upgrade;
                return ScreenKind.DeckGrid;
            case NDeckTransformSelectScreen:           // New Leaf and other transform gifts
                grid = DeckGridKind.Transform;
                return ScreenKind.DeckGrid;
            case NDeckCardSelectScreen deckSelect:     // generic deck pick: removal when its prompt is the removal prompt
                grid = UiDriver.IsRemovalPrompt(deckSelect) ? DeckGridKind.Remove : DeckGridKind.Other;
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

    /// <summary>Opens this player's merchant rug, as talking to the merchant does.</summary>
    public static bool OpenShop()
    {
        try
        {
            var room = NMerchantRoom.Instance;
            if (room?.Inventory is null || room.Inventory.IsOpen) return false;
            var open = HarmonyLib.AccessTools.Method(typeof(NMerchantRoom), "OpenInventory");
            if (open is null) return false;
            open.Invoke(room, null);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't open your shop: {ex.Message}");
            return false;
        }
    }

    /// <summary>Closes a merchant rug the way its back button does (the game keeps Close private).</summary>
    public static void CloseShop(Node inventory)
    {
        try
        {
            if (inventory is NMerchantInventory { IsOpen: true } inv)
                HarmonyLib.AccessTools.Method(typeof(NMerchantInventory), "Close")?.Invoke(inv, null);
        }
        catch (Exception ex) { Log.Warn($"Couldn't close the shop: {ex.Message}"); }
    }

    /// <summary>
    /// Every merchant slot in a fixed order, sold-out ones included (Stocked = false); the view's Index is the slot's position,
    /// so it never shifts when something is bought.
    /// </summary>
    public static List<(ShopItemView View, MerchantEntry Entry)> ShopEntries(out MerchantInventory? inventory)
    {
        var result = new List<(ShopItemView, MerchantEntry)>();
        inventory = MyInventory();
        if (inventory is null) return result;

        void Add(IEnumerable<MerchantEntry> entries, ShopItemType type)
        {
            foreach (var e in entries)
                result.Add((Describe(e, type, result.Count), e));
        }

        Add(inventory.CharacterCardEntries, ShopItemType.Card);
        Add(inventory.ColorlessCardEntries, ShopItemType.ColorlessCard);
        Add(inventory.RelicEntries, ShopItemType.Relic);
        Add(inventory.PotionEntries, ShopItemType.Potion);
        if (inventory.CardRemovalEntry is { } removal)
            result.Add((Describe(removal, ShopItemType.CardRemoval, result.Count), removal));
        return result;
    }

    private static readonly System.Reflection.FieldInfo? RawCostField =
        typeof(MerchantEntry).GetField("_cost", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>The price before relic discounts. The picker's game applies those itself when it shows the copy.</summary>
    public static int RawCost(MerchantEntry e)
    {
        try { return RawCostField?.GetValue(e) is int c ? c : e.Cost; }
        catch { return e.Cost; }
    }

    private static ShopItemView Describe(MerchantEntry e, ShopItemType type, int index)
    {
        if (!e.IsStocked)
            return new ShopItemView(index, type, "", "", "", 0, false, Stocked: false);

        string id = "", title = "Remove a card", desc = "", blob = "";
        bool onSale = false;
        switch (e)
        {
            case MerchantCardEntry card:
                var v = View(card.CreationResult?.Card, withCardData: true);
                id = v.Id; title = v.Title; desc = v.Description; blob = v.Blob;
                onSale = card.IsOnSale;
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
        return new ShopItemView(index, type, id, title, desc, e.Cost, e.EnoughGold, true, RawCost(e), onSale, blob);
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

    /// <summary>
    /// Buys through the merchant exactly as clicking the slot would, and reports how it went: null on success,
    /// otherwise why the game refused (not enough gold, no empty potion slot, ...).
    /// </summary>
    public static async Task<PurchaseStatus?> PurchaseResult(MerchantEntry entry, MerchantInventory inventory, string label)
    {
        PurchaseStatus? failure = null;
        void OnFailed(PurchaseStatus status) => failure = status;
        entry.PurchaseFailed += OnFailed;
        try
        {
            bool ok = await entry.OnTryPurchaseWrapper(inventory);
            if (ok) return null;
            Log.Warn($"Purchase of {label} was refused by the game ({failure?.ToString() ?? "no reason given"})");
            return failure ?? PurchaseStatus.FailureForbidden;
        }
        catch (Exception ex)
        {
            Log.Error($"Purchase of {label} failed", ex);
            return PurchaseStatus.FailureForbidden;
        }
        finally { entry.PurchaseFailed -= OnFailed; }
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

    /// <summary>The selection rules a deck grid was opened with (its private _prefs), if it has them.</summary>
    public static CardSelectorPrefs? Prefs(Node screen)
    {
        for (var t = screen.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField("_prefs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f?.GetValue(screen) is CardSelectorPrefs prefs) return prefs;
        }
        return null;
    }

    /// <summary>How many cards the grid wants: Precarious Shears takes 2, most gifts 1.</summary>
    public static (int Min, int Max) Limits(Node screen)
    {
        try
        {
            if (Prefs(screen) is { } p) return (Math.Max(0, p.MinSelect), Math.Max(1, p.MaxSelect));
        }
        catch { /* fall through */ }
        return (1, 1);
    }

    /// <summary>The generic deck screen is also used for duplicating and other picks; only its removal prompt is a removal.</summary>
    public static bool IsRemovalPrompt(Node screen)
    {
        try
        {
            if (Prefs(screen) is not { } p) return false;
            var removal = CardSelectorPrefs.RemoveSelectionPrompt;
            return p.Prompt is { } prompt && prompt.LocTable == removal.LocTable && prompt.LocEntryKey == removal.LocEntryKey;
        }
        catch { return false; }
    }

    /// <summary>The packs on a choose-a-pack screen, in on-screen order.</summary>
    public static List<NCardBundle> Bundles(Node screen) =>
        Nodes.FindAll<NCardBundle>(screen).Where(b => b.Bundle is { Count: > 0 }).ToList();

    /// <summary>Every card of every pack (with card data) plus how many cards each pack has.</summary>
    public static (List<CardView> Cards, List<int> Groups) BundleOptions(Node screen)
    {
        var cards = new List<CardView>();
        var groups = new List<int>();
        foreach (var bundle in Bundles(screen))
        {
            foreach (var card in bundle.Bundle) cards.Add(GameQuery.View(card, withCardData: true));
            groups.Add(bundle.Bundle.Count);
        }
        return (cards, groups);
    }

    /// <summary>Opens the pack's preview, as clicking it would; ConfirmWhenEnabled then takes it.</summary>
    public static bool PressBundle(Node screen, int index)
    {
        var bundles = Bundles(screen);
        if (index < 0 || index >= bundles.Count) return false;
        var bundle = bundles[index];
        bundle.EmitSignal(NCardBundle.SignalName.Clicked, bundle);
        return true;
    }

    /// <summary>
    /// If this player's rewards screen is on top and still has a card reward they haven't opened or skipped,
    /// opens it as clicking it would. <paramref name="tried"/> keeps one reward from being opened twice.
    /// </summary>
    public static bool OpenOwnCardReward(HashSet<ulong> tried)
    {
        if (GameQuery.TopOverlay() is not NRewardsScreen rewards) return false;

        var skipped = new HashSet<Control>();
        try
        {
            if (typeof(NRewardsScreen).GetField("_skippedRewardButtons", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(rewards) is IEnumerable<Control> list)
                skipped.UnionWith(list);
        }
        catch { /* best effort: tried still stops repeats */ }

        var button = Nodes.FindAll<NRewardButton>(rewards).FirstOrDefault(b =>
            b.Reward is CardReward { SuccessfullySelected: false }
            && b.IsEnabled && b.IsVisibleInTree()
            && !skipped.Contains(b) && !tried.Contains(b.GetInstanceId()));
        if (button is null) return false;

        tried.Add(button.GetInstanceId());
        button.ForceClick();
        return true;
    }

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
