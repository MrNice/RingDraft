using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Read-only questions about the game. Type and member names marked "confirmed" come from
/// public open-source StS2 mods; "VERIFY" means a reflection guess to check against a decompile.
/// </summary>
internal static class GameQuery
{
    public static List<ulong> SeatOrder()
    {
        try
        {
            var state = RunManager.Instance?.DebugOnlyGetState(); // confirmed (sts2_typing)
            return state?.Players.Select(p => p.NetId).ToList() ?? new List<ulong>();
        }
        catch { return new List<ulong>(); }
    }

    public static bool InCombat()
    {
        var cm = CombatManager.Instance; // confirmed (sts2-ai-mod)
        return cm is not null && cm.IsInProgress && !cm.IsOverOrEnding;
    }

    public static RoomKind Room()
    {
        if (InCombat()) return RoomKind.Combat;
        var room = RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom; // confirmed
        switch (room)
        {
            case null: return RoomKind.Other;
            case MerchantRoom: return RoomKind.Merchant;   // confirmed
            case RestSiteRoom: return RoomKind.RestSite;   // confirmed
        }
        string name = room.GetType().Name;                  // VERIFY: event/treasure room class names
        if (name.Contains("Event", StringComparison.OrdinalIgnoreCase)) return RoomKind.Event;
        if (name.Contains("Treasure", StringComparison.OrdinalIgnoreCase)) return RoomKind.Treasure;
        return RoomKind.Other;
    }

    public static Node? TopOverlay() => NOverlayStack.Instance?.Peek() as Node; // confirmed

    public static Node? ShopInventoryNode()
    {
        var inv = NMerchantRoom.Instance?.Inventory; // confirmed
        return inv?.IsOpen == true ? inv as Node : null;
    }

    public static ScreenKind Classify(Node? overlay, out Node? screen)
    {
        screen = overlay;
        switch ((object?)overlay)
        {
            case NCardRewardSelectionScreen: return ScreenKind.CardReward;   // confirmed
            case NCardGridSelectionScreen: return ScreenKind.DeckGrid;       // confirmed
            case NRewardsScreen: return ScreenKind.Other;                    // gold/potion/relic list stays with owner
            case not null when Nodes.FindAll<NCardHolder>(overlay!).Count > 0:
                return ScreenKind.GeneratedCardChoice;                       // potion / Toolbox "choose 1 of 3"
            case not null:
                return ScreenKind.Other;
        }

        screen = ShopInventoryNode();
        return screen is not null ? ScreenKind.ShopInventory : ScreenKind.None;
    }

    public static Player? Me()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        return state is null ? null : LocalContext.GetMe(state.Players); // confirmed
    }

    public static int MyGold() => Me()?.Gold ?? 0; // confirmed

    public static List<CardModel> MyDeck()
    {
        var deck = Reflect.Get(Me(), "Deck", "MasterDeck");            // VERIFY
        var cards = Reflect.Get(deck, "Cards") ?? deck;
        return Reflect.Items(cards).OfType<CardModel>().ToList();
    }

    public static List<string> MyRelics()
    {
        var relics = Reflect.Get(Me(), "Relics");                       // VERIFY
        return Reflect.Items(relics).Select(r => Reflect.Text(Reflect.Get(r, "Title"))).Where(s => s.Length > 0).ToList();
    }

    public static CardView View(CardModel? card)
    {
        if (card is null) return new CardView("", "?", "", 0);
        string title;
        try { title = card.Title.ToString() ?? "?"; } catch { title = "?"; } // confirmed
        return new CardView(
            card.GetType().FullName ?? card.GetType().Name,
            Reflect.Text(title),
            Reflect.Text(Reflect.Get(card, "Description")),             // VERIFY (BetterSpire2 notes GetFormattedText)
            Reflect.GetInt(card, 0, "CurrentUpgradeLevel", "UpgradeLevel", "TimesUpgraded", "IsUpgraded"));
    }

    public static List<CardView> MyDeckViews() => MyDeck().Select(View).ToList();

    /// <summary>Stocked shop entries in a stable order; the view's Index is the position in this list.</summary>
    public static List<(ShopItemView View, MerchantEntry Entry)> ShopEntries(out object? inventory)
    {
        var result = new List<(ShopItemView, MerchantEntry)>();
        inventory = null;
        if (RunManager.Instance?.DebugOnlyGetState()?.CurrentRoom is not MerchantRoom room) return result;
        var inv = room.Inventory; // confirmed
        inventory = inv;
        if (inv is null) return result;

        void Add(IEnumerable<MerchantEntry>? entries, ShopItemType type)
        {
            if (entries is null) return;
            foreach (var e in entries)
                if (e is not null && e.IsStocked)
                    result.Add((Describe(e, type, result.Count), e));
        }

        Add(inv.CharacterCardEntries, ShopItemType.Card);           // confirmed names
        Add(inv.ColorlessCardEntries, ShopItemType.ColorlessCard);
        Add(inv.RelicEntries, ShopItemType.Relic);
        Add(inv.PotionEntries, ShopItemType.Potion);
        if (inv.CardRemovalEntry is { IsStocked: true } removal)
            result.Add((Describe(removal, ShopItemType.CardRemoval, result.Count), removal));
        return result;
    }

    private static ShopItemView Describe(MerchantEntry e, ShopItemType type, int index)
    {
        string id = "", title = "Remove a card", desc = "";
        switch (e)
        {
            case MerchantCardEntry card:                                // confirmed
                var v = View(card.CreationResult.Card);
                id = v.Id; title = v.Title; desc = v.Description;
                break;
            case MerchantRelicEntry relic:
                id = relic.Model.GetType().FullName ?? ""; title = Reflect.Text(relic.Model.Title);
                desc = Reflect.Text(Reflect.Get(relic.Model, "Description"));
                break;
            case MerchantPotionEntry potion:
                id = potion.Model.GetType().FullName ?? ""; title = Reflect.Text(potion.Model.Title);
                desc = Reflect.Text(Reflect.Get(potion.Model, "Description"));
                break;
        }
        return new ShopItemView(index, type, id, title, desc, Convert.ToInt32((object)e.Cost), e.EnoughGold);
    }

    public static bool Purchase(MerchantEntry entry, object? inventory)
    {
        try
        {
            if (Reflect.TryInvoke(entry, "OnTryPurchaseWrapper", inventory)) return true; // confirmed (sts2-ai-mod)
            Log.Warn("OnTryPurchaseWrapper not found on merchant entry");
        }
        catch (Exception ex) { Log.Error("Purchase failed", ex); }
        return false;
    }

    public static List<NRestSiteButton> RestButtons()
    {
        var root = NGame.Instance?.RootSceneContainer?.CurrentScene; // confirmed (sts2_typing)
        return root is null ? new List<NRestSiteButton>() : Nodes.FindAll<NRestSiteButton>(root);
    }

    public static string? RestOptionId(NRestSiteButton b)
    {
        try { return b.Option.OptionId; } catch { return null; } // confirmed
    }
}

/// <summary>Performs choices on the owner's own screens, the same way a click would, so vanilla sync carries them.</summary>
internal static class UiDriver
{
    public static List<NCardHolder> Holders(Node screen) =>
        (object)screen is NCardGridSelectionScreen
            ? Nodes.FindAll<NGridCardHolder>(screen).Cast<NCardHolder>().ToList()
            : Nodes.FindAll<NCardHolder>(screen);

    public static List<CardView> Options(Node screen) =>
        Holders(screen).Select(h => GameQuery.View(h.CardNode?.Model)).ToList(); // confirmed

    /// <summary>Selection screens finish setting up a frame or two after they appear (sts2-ai-mod waits on _completionSource).</summary>
    public static bool IsReady(Node screen)
    {
        for (var t = screen.GetType(); t is not null; t = t.BaseType)
        {
            var f = t.GetField("_completionSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f is not null) return f.GetValue(screen) is not null;
        }
        return true;
    }

    public static bool HasSkip(Node screen) =>
        Nodes.FindAll<NCardRewardAlternativeButton>(screen).Count > 0
        || Nodes.FindFirst<NChoiceSelectionSkipButton>(screen) is not null;

    public static bool PressHolder(Node screen, int index)
    {
        var holders = Holders(screen);
        if (index < 0 || index >= holders.Count) return false;
        var holder = holders[index];
        var grid = (object)screen is NCardGridSelectionScreen ? Nodes.FindFirst<NCardGrid>(screen) : null;
        if (grid is not null) grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder); // confirmed
        else holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);                     // confirmed
        return true;
    }

    public static bool PressSkip(Node screen)
    {
        // Reward screens: first alternative button is Skip (Reroll comes second when present).
        var alt = Nodes.FindAll<NCardRewardAlternativeButton>(screen);
        if (alt.Count > 0) return Click(alt[0]);
        var skip = Nodes.FindFirst<NChoiceSelectionSkipButton>(screen);
        return skip is not null && Click(skip);
    }

    public static bool ClickConfirmIfEnabled(Node screen)
    {
        var btn = Nodes.FindAll<NConfirmButton>(screen).FirstOrDefault(b => b.IsEnabled);
        return btn is not null && Click(btn);
    }

    /// <summary>Synthesizes a real left click at the control's centre. VERIFY if a button ignores it.</summary>
    public static bool Click(object node)
    {
        if (node is not Control c || !Nodes.Alive(c)) return false;
        var vp = c.GetViewport();
        var pos = c.GetGlobalTransformWithCanvas() * (c.Size / 2f);
        vp.PushInput(new InputEventMouseMotion { Position = pos, GlobalPosition = pos }, true);
        vp.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = true, Position = pos, GlobalPosition = pos }, true);
        vp.PushInput(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = false, Position = pos, GlobalPosition = pos }, true);
        return true;
    }
}
