using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Vfx;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Rooms;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// The picker's copy of the owner's shop, on the game's own merchant rug. It is built from the owner's
/// ShopStateMsg, never from the picker's own copy of the run, and nothing on it can buy anything: a click
/// becomes a ShopActionMsg, and the owner's client makes the real purchase and sends back the new state.
/// </summary>
internal sealed class MimicShop
{
    public sealed record Actions(Action<int> Buy, Action<int> Remove, Action Done, Action<Node> PickerScreenOpened);

    /// <summary>The copy on screen, if any. The purchase patches below check inventories against it.</summary>
    public static MimicShop? Current { get; private set; }

    public ulong Owner { get; }
    public uint RequestId { get; }
    public NMerchantInventory Node { get; }
    public MerchantInventory Inventory { get; }
    /// <summary>The owner's gold and potion belt over the rug, if they could be shown.</summary>
    public MimicPurse? Purse => _purse;

    private readonly Actions _actions;
    private readonly Player _owner;
    private readonly string _ownerName;
    private readonly Dictionary<int, MerchantEntry> _slots;
    private readonly Label _header;
    private MimicPurse? _purse;
    private ShopStateMsg _state;
    private (int Index, string Id, string Title)? _pendingBuy;
    private double _busyUntil;
    private bool _closing;
    private Node? _removalScreen;

    private MimicShop(ShopStateMsg s, Player owner, string ownerName, NMerchantInventory node, MerchantInventory inventory,
        Dictionary<int, MerchantEntry> slots, Label header, Actions actions)
    {
        Owner = s.From;
        RequestId = s.RequestId;
        _state = s;
        _owner = owner;
        _ownerName = ownerName;
        Node = node;
        Inventory = inventory;
        _slots = slots;
        _header = header;
        _actions = actions;
    }

    public static bool CanOpen() => NMerchantRoom.Instance is { } room && Nodes.Alive(room) && room.Room is not null;

    /// <summary>Lays the owner's shop out on a second merchant rug, above the picker's own. Null if anything is missing.</summary>
    public static MimicShop? Open(ShopStateMsg s, string ownerName, Actions actions)
    {
        if (Current is not null) Current.Close();
        var room = NMerchantRoom.Instance;
        var owner = GameQuery.PlayerById(s.From);
        if (room?.Room is null || owner is null) return null;

        var (inventory, slots) = Build(s, owner);
        if (inventory is null) return null;

        var node = SceneHelper.Instantiate<NMerchantInventory>("merchant/merchant_inventory");
        node.Name = "RingDraftShop";
        var own = room.Inventory;
        var parent = own?.GetParent() ?? room;
        parent.AddChild(node);
        if (own is not null) parent.MoveChild(node, own.GetIndex() + 1);

        try
        {
            node.Initialize(inventory, MerchantRoom.Dialogue);
        }
        catch (Exception ex)
        {
            Log.Error("Setting up the copy of the shop failed", ex);
            node.QueueFree();
            return null;
        }

        var header = Style.Label("", 24, Style.Accent);
        header.HorizontalAlignment = HorizontalAlignment.Center;
        header.MouseFilter = Control.MouseFilterEnum.Ignore;
        header.AnchorLeft = 0; header.AnchorRight = 1; header.OffsetTop = 18; header.OffsetBottom = 60;
        node.AddChild(header);

        var shop = new MimicShop(s, owner, ownerName, node, inventory, slots, header, actions);
        try { shop._purse = MimicPurse.Create(node, owner); }
        catch (Exception ex) { Log.Error("Showing the owner's gold and potions failed", ex); }
        header.Visible = shop._purse is null; // the owner's purse in the top bar replaces the text header; it's the fallback
        Current = shop;
        node.Connect(NMerchantInventory.SignalName.InventoryClosed, Callable.From(shop.OnInventoryClosed));
        node.Open();
        shop.UpdateHeader();
        ActiveScreenContext.Instance?.Update();
        return shop;
    }

    /// <summary>
    /// Applies a fresh state from the owner: sold items disappear, restocked ones appear, prices follow the owner's gold.
    /// Returns false if the shop's layout changed, in which case the caller opens a new copy.
    /// </summary>
    public bool Refresh(ShopStateMsg s)
    {
        if (s.From != Owner || s.RequestId != RequestId) return false;
        if (s.Items.Count != _slots.Count || s.Items.Any(i => !_slots.ContainsKey(i.Index))) return false;

        // A purchase the owner's game refused (not enough gold, no empty potion slot): the item is still there.
        (MerchantEntry Entry, PurchaseStatus Status)? refusal = null;
        if (_pendingBuy is { } pending)
        {
            _pendingBuy = null;
            var again = s.Items.FirstOrDefault(i => i.Index == pending.Index);
            if (again is { Stocked: true } && again.Id == pending.Id && again.Title == pending.Title && _slots.TryGetValue(pending.Index, out var refused))
            {
                var status = PurchaseStatus.FailureForbidden;
                if (refused is MerchantPotionEntry && !_owner.HasOpenPotionSlots) status = PurchaseStatus.FailureSpace;
                else if (!again.Affordable) status = PurchaseStatus.FailureGold;
                refusal = (refused, status);
                Log.Info($"{_ownerName}'s game refused {pending.Title} ({status})");
            }
        }

        bool removalUsedNow = false;
        foreach (var item in s.Items)
        {
            var entry = _slots[item.Index];
            bool wasStocked = entry.IsStocked;
            if (!Fill(entry, item, _owner)) return false;
            if (item.Type == ShopItemType.CardRemoval && wasStocked && !item.Stocked) removalUsedNow = true;
            EntryUpdated(entry);
        }
        if (removalUsedNow) Call(Node, "OnCardRemovalUsed");
        // The item is back in its slot; now the game's own "can't buy that" reaction on it.
        if (refusal is { } r)
        {
            Failed(r.Entry, r.Status);
            if (r.Status == PurchaseStatus.FailureSpace) _purse?.PotionsFull();
        }

        _state = s;
        _busyUntil = 0;
        UpdateHeader(s.Notice);
        return true;
    }

    /// <summary>Takes the copy down without telling the owner anything.</summary>
    public void Close()
    {
        if (_closing) return;
        _closing = true;
        if (Current == this) Current = null;
        if (_removalScreen is not null) NativeScreens.Close(_removalScreen);
        _purse?.AnimateOut();
        try
        {
            if (Nodes.Alive(Node))
            {
                if (Node.IsOpen) Call(Node, "Close"); // the game keeps Close private; its back button calls it
                var timer = Node.GetTree()?.CreateTimer(0.6);
                if (timer is not null) timer.Timeout += () => { if (Nodes.Alive(Node)) Node.QueueFree(); };
                else Node.QueueFree();
            }
        }
        catch (Exception ex) { Log.Error("Closing the copy of the shop failed", ex); }
        ActiveScreenContext.Instance?.Update();
    }

    // ------------------------------------------------------------------ clicks

    /// <summary>A click on one of the copy's slots. Never buys anything here.</summary>
    internal Task<bool> OnClick(MerchantEntry entry)
    {
        int index = _slots.FirstOrDefault(kv => ReferenceEquals(kv.Value, entry)).Key;
        if (!_slots.TryGetValue(index, out var found) || !ReferenceEquals(found, entry)) return Task.FromResult(false);
        if (SourceTracker.Now() < _busyUntil || _removalScreen is not null) return Task.FromResult(false);

        var view = _state.Items.FirstOrDefault(i => i.Index == index);
        if (!entry.IsStocked) { Failed(entry, PurchaseStatus.FailureOutOfStock); return Task.FromResult(false); }
        // Both checks: this client's copy of the owner's gold, and the owner's own verdict from their last update.
        if (!entry.EnoughGold || view is { Affordable: false })
        {
            Failed(entry, PurchaseStatus.FailureGold);
            UpdateHeader($"{_ownerName} can't afford that.");
            return Task.FromResult(false);
        }
        if (entry is MerchantPotionEntry && !_owner.HasOpenPotionSlots)
        {
            Failed(entry, PurchaseStatus.FailureSpace);
            _purse?.PotionsFull();
            UpdateHeader($"{_ownerName}'s potion belt is full.");
            return Task.FromResult(false);
        }

        if (entry is MerchantCardRemovalEntry)
        {
            _ = ChooseRemoval();
            return Task.FromResult(false);
        }

        _busyUntil = SourceTracker.Now() + 5;
        UpdateHeader(view is null ? "" : $"Buying {view.Title}…");
        if (view is not null) _pendingBuy = (index, view.Id, view.Title);
        _actions.Buy(index);

        // Show the purchase the way the game does, but flying to the owner's row in the player list.
        // The slot empties now; if the owner's client refuses the purchase, the next state puts the item back.
        if (view is not null)
        {
            MimicFly.ToOwner(this, entry);
            Fill(entry, view with { Stocked = false }, _owner);
            EntryUpdated(entry);
            try { SfxCmd.Play("event:/sfx/npcs/merchant/merchant_thank_yous", 1f); } catch { /* sound is a nicety */ }
        }
        return Task.FromResult(false);
    }

    /// <summary>The removal slot: pick the card from the owner's deck on the game's own deck screen.</summary>
    private async Task ChooseRemoval()
    {
        var deck = _owner.Deck?.Cards.ToList() ?? new List<CardModel>();
        if (deck.Count == 0) return;

        NDeckCardSelectScreen screen;
        try
        {
            var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, 1) { Cancelable = true };
            screen = NDeckCardSelectScreen.Create(deck, prefs);
            NOverlayStack.Instance!.Push(screen);
        }
        catch (Exception ex)
        {
            Log.Error("Opening the removal screen failed", ex);
            return;
        }
        _removalScreen = screen;
        _actions.PickerScreenOpened(screen);

        CardModel? picked = null;
        try { picked = (await screen.CardsSelected())?.FirstOrDefault(); }
        catch (Exception ex) { Log.Error("Removal screen failed", ex); }
        _removalScreen = null;
        if (Nodes.Alive(screen) && NOverlayStack.Instance?.Peek() == screen) NativeScreens.Close(screen);
        if (picked is null || _closing) return;

        int deckIndex = DeckIndex(deck, picked);
        if (deckIndex < 0)
        {
            Log.Warn($"Couldn't find {picked.Title} in {_ownerName}'s deck list");
            UpdateHeader("Couldn't match that card. Try again.");
            return;
        }
        _busyUntil = SourceTracker.Now() + 5;
        UpdateHeader($"Removing {picked.Title}…");
        _actions.Remove(deckIndex);
    }

    /// <summary>The card's position in the deck list the owner sent, checked against the card itself.</summary>
    private int DeckIndex(List<CardModel> deck, CardModel picked)
    {
        var view = GameQuery.View(picked);
        int local = deck.IndexOf(picked);
        if (local >= 0 && local < _state.Deck.Count && Matches(_state.Deck[local], view)) return local;
        for (int i = 0; i < _state.Deck.Count; i++)
            if (Matches(_state.Deck[i], view)) return i;
        return -1;
    }

    private static bool Matches(CardView a, CardView b) => a.Id == b.Id && a.Upgrades == b.Upgrades && a.Title == b.Title;

    private void OnInventoryClosed()
    {
        if (_closing) return;
        Log.Info($"Closed {_ownerName}'s shop: done shopping");
        Close();
        _actions.Done();
    }

    private void UpdateHeader(string notice = "")
    {
        if (_purse is not null) return; // the owner's coin, gold and belt in the top bar say whose shop this is
        string text = $"Shopping for {_ownerName} · {_owner.Gold} gold (theirs)";
        if (!string.IsNullOrEmpty(notice)) text += $"\n{notice}";
        _header.Text = text;
    }

    // ------------------------------------------------------------------ building the copy

    private static (MerchantInventory?, Dictionary<int, MerchantEntry>) Build(ShopStateMsg s, Player owner)
    {
        var slots = new Dictionary<int, MerchantEntry>();
        try
        {
            var inventory = new MerchantInventory(owner);
            var characterCards = Get<List<MerchantCardEntry>>(inventory, "_characterCardEntries");
            var colorlessCards = Get<List<MerchantCardEntry>>(inventory, "_colorlessCardEntries");
            var relics = Get<List<MerchantRelicEntry>>(inventory, "_relicEntries");
            var potions = Get<List<MerchantPotionEntry>>(inventory, "_potionEntries");

            foreach (var item in s.Items.OrderBy(i => i.Index))
            {
                MerchantEntry entry;
                switch (item.Type)
                {
                    case ShopItemType.Card:
                    case ShopItemType.ColorlessCard:
                        var card = Blank<MerchantCardEntry>(owner);
                        Set(card, typeof(MerchantCardEntry), "_inventory", inventory);
                        (item.Type == ShopItemType.Card ? characterCards : colorlessCards).Add(card);
                        entry = card;
                        break;
                    case ShopItemType.Relic:
                        var relic = Blank<MerchantRelicEntry>(owner);
                        relics.Add(relic);
                        entry = relic;
                        break;
                    case ShopItemType.Potion:
                        var potion = Blank<MerchantPotionEntry>(owner);
                        potions.Add(potion);
                        entry = potion;
                        break;
                    case ShopItemType.CardRemoval:
                        var removal = new MerchantCardRemovalEntry(owner);
                        Set(inventory, typeof(MerchantInventory), "<CardRemovalEntry>k__BackingField", removal);
                        entry = removal;
                        break;
                    default:
                        continue;
                }
                if (!Fill(entry, item, owner)) return (null, slots);
                slots[item.Index] = entry;
            }
            return (inventory, slots);
        }
        catch (Exception ex)
        {
            Log.Error("Building the copy of the shop failed", ex);
            return (null, slots);
        }
    }

    /// <summary>
    /// An entry with no constructor run: the real constructors roll prices and stock from the owner's
    /// shop RNG, which must never move on the picker's client.
    /// </summary>
    private static T Blank<T>(Player owner) where T : MerchantEntry
    {
        var entry = (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));
        Set(entry, typeof(MerchantEntry), "_player", owner);
        return entry;
    }

    /// <summary>Puts the slot's item (or nothing, when sold out) and price into the entry.</summary>
    private static bool Fill(MerchantEntry entry, ShopItemView item, Player owner)
    {
        Set(entry, typeof(MerchantEntry), "_cost", item.BaseCost);
        switch (entry)
        {
            case MerchantCardEntry card:
            {
                CardCreationResult? result = null;
                if (item.Stocked)
                {
                    var current = card.CreationResult?.Card;
                    if (current is not null && GameQuery.View(current).Id == item.Id && GameQuery.View(current).Title == item.Title)
                        result = card.CreationResult;
                    else
                    {
                        var model = CardCodec.Rebuild(item.Blob, owner);
                        if (model is null) return false;
                        result = new CardCreationResult(model);
                    }
                }
                Set(card, typeof(MerchantCardEntry), "<CreationResult>k__BackingField", result);
                Set(card, typeof(MerchantCardEntry), "<IsOnSale>k__BackingField", item.Stocked && item.OnSale);
                return true;
            }
            case MerchantRelicEntry relic:
            {
                RelicModel? model = null;
                if (item.Stocked)
                {
                    model = relic.Model is { } cur && cur.GetType().FullName == item.Id ? cur : Model<RelicModel>(item.Id);
                    if (model is null) return false;
                }
                Set(relic, typeof(MerchantRelicEntry), "<Model>k__BackingField", model);
                return true;
            }
            case MerchantPotionEntry potion:
            {
                PotionModel? model = null;
                if (item.Stocked)
                {
                    model = potion.Model is { } cur && cur.GetType().FullName == item.Id ? cur : Model<PotionModel>(item.Id);
                    if (model is null) return false;
                }
                Set(potion, typeof(MerchantPotionEntry), "<Model>k__BackingField", model);
                return true;
            }
            case MerchantCardRemovalEntry removal:
                Set(removal, typeof(MerchantCardRemovalEntry), "<Used>k__BackingField", !item.Stocked);
                return true;
            default:
                return false;
        }
    }

    private static T? Model<T>(string typeName) where T : AbstractModel
    {
        var type = typeof(T).Assembly.GetType(typeName);
        if (type is null)
        {
            Log.Warn($"Unknown shop item {typeName}");
            return null;
        }
        return ModelDb.GetByIdOrNull<T>(ModelDb.GetId(type))?.MutableClone() as T;
    }

    // ------------------------------------------------------------------ reflection helpers

    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static T Get<T>(object target, string field) =>
        (T)(target.GetType().GetField(field, Any) ?? throw new MissingFieldException(target.GetType().Name, field)).GetValue(target)!;

    private static void Set(object target, Type declaring, string field, object? value) =>
        (declaring.GetField(field, Any) ?? throw new MissingFieldException(declaring.Name, field)).SetValue(target, value);

    private static void EntryUpdated(MerchantEntry entry)
    {
        try { (typeof(MerchantEntry).GetField("EntryUpdated", Any)?.GetValue(entry) as Action)?.Invoke(); }
        catch (Exception ex) { Log.Warn($"Refreshing a shop slot failed: {ex.Message}"); }
    }

    private static void Failed(MerchantEntry entry, PurchaseStatus status)
    {
        try { AccessTools.Method(typeof(MerchantEntry), "InvokePurchaseFailed")?.Invoke(entry, new object[] { status }); }
        catch (Exception ex) { Log.Warn($"Couldn't show the purchase failure: {ex.Message}"); }
    }

    private static void Call(object target, string method)
    {
        try { AccessTools.Method(target.GetType(), method)?.Invoke(target, null); }
        catch (Exception ex) { Log.Warn($"{method} failed: {ex.Message}"); }
    }
}

/// <summary>
/// A bought item's icon flies from the shop slot to the owner's row in the player list. The row's own
/// "obtained" pop (played when the purchase syncs) waits until the icon lands.
/// </summary>
internal static class MimicFly
{
    private static readonly Dictionary<ulong, Task> Landing = new();
    [ThreadStatic] internal static bool Bypass;
    private static CanvasLayer? _layer;

    /// <summary>
    /// Cards use the game's own card flight (NCardFlyVfx): the character's trail, shrinking, turning head-first along
    /// an arc. It normally aims at a pile on the local top bar; here its end point is the owner's row instead, and it
    /// isn't "adding to a pile", so nothing touches the owner's piles on this client.
    /// </summary>
    private static bool FlyCard(NCard card, ulong owner, NMultiplayerPlayerState row)
    {
        var global = NRun.Instance?.GlobalUi;
        var trail = GameQuery.PlayerById(owner)?.Character?.TrailPath;
        if (global?.TopBar?.TrailContainer is not { } trails || trail is null) return false;

        var rowXf = row.GetGlobalTransformWithCanvas();
        var target = rowXf.Origin + row.Size * rowXf.Scale * new Vector2(0.25f, 0.5f);

        global.ReparentCard(card); // onto the global UI, where the game flies cards from
        var vfx = NCardFlyVfx.Create(card, PileType.Deck, false, trail);
        if (vfx is null) return false;
        typeof(NCardFlyVfx).GetField("_endPos", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(vfx, target);

        var done = new TaskCompletionSource();
        Landing[owner] = done.Task;
        card.Connect(Node.SignalName.TreeExited, Callable.From(() => done.TrySetResult()), (uint)GodotObject.ConnectFlags.OneShot);
        card.GetTree().CreateTimer(1.2).Timeout += () => done.TrySetResult();
        trails.AddChild(vfx);
        try { SfxCmd.Play("event:/sfx/ui/cards/card_movement_B_into_deck", 1f); } catch { /* sound is a nicety */ }
        return true;
    }

    public static bool TryLanding(ulong player, out Task landing)
    {
        if (Landing.TryGetValue(player, out var t) && !t.IsCompleted) { landing = t; return true; }
        landing = Task.CompletedTask;
        return false;
    }

    public static void ToOwner(MimicShop shop, MerchantEntry entry)
    {
        try
        {
            var slot = Nodes.FindAll<NMerchantSlot>(shop.Node).FirstOrDefault(s => ReferenceEquals(s.Entry, entry));
            string? field = slot switch
            {
                NMerchantRelic => "_relicNode",
                NMerchantPotion => "_potionNode",
                NMerchantCard => "_cardNode",
                _ => null,
            };
            var info = field is null ? null : slot!.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (info?.GetValue(slot) is not CanvasItem visual || !Nodes.Alive(visual)) return;
            // Potions fly into the owner's belt over the rug; everything else into their row in the player list.
            Vector2? target = slot is NMerchantPotion ? shop.Purse?.NextPotionSlot() : null;
            // Take the icon out of the slot so the slot's own refresh doesn't free it mid-flight.
            if (Fly(visual, shop.Owner, target))
            {
                info.SetValue(slot, null);
                AccessTools.Method(typeof(NMerchantSlot), "ClearHoverTip")?.Invoke(slot, null);
            }
        }
        catch (Exception ex) { Log.Warn($"Purchase animation failed: {ex.Message}"); }
    }

    /// <summary>
    /// Lifts a card, relic or potion node off its screen and sends it, shrinking, into the owner's row in the
    /// player list. Returns false (and leaves the node alone) if there is no row to fly to.
    /// </summary>
    public static bool Fly(CanvasItem visual, ulong owner, Vector2? to = null)
    {
        try
        {
            if (!Nodes.Alive(visual) || visual.GetTree() is not { } tree) return false;
            var row = Nodes.FindAll<NMultiplayerPlayerState>(tree.Root).FirstOrDefault(r => r.Player?.NetId == owner && r.IsVisibleInTree());
            if (row is null && to is null) return false;

            if (visual is NCard card && row is not null) return FlyCard(card, owner, row);

            var start = visual.GetGlobalTransformWithCanvas();
            Vector2 target;
            if (to is { } point)
            {
                // Aim the icon's centre at the point (the icon is positioned by its top-left corner).
                var half = visual is Control vc ? vc.Size * start.Scale * 0.5f : Vector2.Zero;
                target = point - half * 0.6f;
            }
            else
            {
                var rowXf = row!.GetGlobalTransformWithCanvas();
                target = rowXf.Origin + row.Size * rowXf.Scale * new Vector2(0.25f, 0.5f);
            }

            if (_layer is null || !Nodes.Alive(_layer))
            {
                _layer = new CanvasLayer { Name = "RingDraftFly", Layer = 120 };
                tree.Root.AddChild(_layer);
            }
            visual.GetParent()?.RemoveChild(visual);
            _layer.AddChild(visual);
            if (visual is Control c) { c.MouseFilter = Control.MouseFilterEnum.Ignore; c.GlobalPosition = start.Origin; c.Scale = start.Scale; }
            else if (visual is Node2D n) { n.GlobalPosition = start.Origin; n.Scale = start.Scale; }

            var done = new TaskCompletionSource();
            Landing[owner] = done.Task;
            // Cards are Node2D and large; relic/potion icons are small Controls. Into the belt, a potion stays potion-sized.
            float endScale = to is not null ? 0.6f : visual is Node2D ? 0.12f : 0.45f;
            var tween = visual.CreateTween();
            tween.SetParallel();
            tween.TweenProperty(visual, "global_position", target, 0.6).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            tween.TweenProperty(visual, "scale", start.Scale * endScale, 0.6).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
            tween.TweenProperty(visual, "modulate:a", 0.0f, 0.15).SetDelay(to is not null ? 0.55 : 0.5);
            tween.Chain().TweenCallback(Callable.From(() =>
            {
                done.TrySetResult();
                if (Nodes.Alive(visual)) visual.QueueFree();
            }));
            // Never hold the row's animation for long, whatever happens to the tween.
            tree.CreateTimer(1.5).Timeout += () => done.TrySetResult();
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Fly animation failed: {ex.Message}");
            return false;
        }
    }
}

/// <summary>Holds the player row's "obtained" pop until the flying icon arrives.</summary>
[HarmonyPatch]
internal static class MimicLandingPatch
{
    private static IEnumerable<MethodBase> TargetMethods() =>
        new[] { "AnimateRelicObtained", "AnimatePotionObtained", "AnimateCardObtained" }
            .Select(name => AccessTools.Method(typeof(NMultiplayerPlayerState), name))
            .Where(m => m is not null)!;

    private static bool Prefix(NMultiplayerPlayerState __instance, object __0, MethodBase __originalMethod, ref Task __result)
    {
        if (MimicFly.Bypass || __instance.Player is not { } player || !MimicFly.TryLanding(player.NetId, out var landing)) return true;
        __result = AfterLanding(__instance, __0, __originalMethod, landing);
        return false;
    }

    private static async Task AfterLanding(NMultiplayerPlayerState row, object model, MethodBase original, Task landing)
    {
        await landing;
        if (!Nodes.Alive(row)) return;
        Task? animation;
        MimicFly.Bypass = true;
        try { animation = original.Invoke(row, new[] { model }) as Task; }
        finally { MimicFly.Bypass = false; }
        if (animation is not null) await animation;
    }
}

/// <summary>Clicks on the copy of a shop never reach the real purchase code.</summary>
[HarmonyPatch(typeof(MerchantEntry), nameof(MerchantEntry.OnTryPurchaseWrapper))]
internal static class MimicPurchasePatch
{
    private static bool Prefix(MerchantEntry __instance, MerchantInventory __0, ref Task<bool> __result)
    {
        if (MimicShop.Current is not { } shop || !ReferenceEquals(__0, shop.Inventory)) return true;
        __result = shop.OnClick(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(MerchantCardRemovalEntry), nameof(MerchantCardRemovalEntry.OnTryPurchaseWrapper),
    new[] { typeof(MerchantInventory), typeof(bool), typeof(bool) })]
internal static class MimicRemovalPatch
{
    private static bool Prefix(MerchantCardRemovalEntry __instance, MerchantInventory __0, ref Task<bool> __result)
    {
        if (MimicShop.Current is not { } shop || !ReferenceEquals(__0, shop.Inventory)) return true;
        __result = shop.OnClick(__instance);
        return false;
    }
}

/// <summary>
/// While the copy is open it is the screen in front, so its back button and controller navigation work
/// (the game disables a merchant rug's back button when it isn't the active screen).
/// </summary>
[HarmonyPatch(typeof(ActiveScreenContext), nameof(ActiveScreenContext.GetCurrentScreen))]
internal static class MimicActiveScreenPatch
{
    private static void Postfix(ref IScreenContext __result)
    {
        if (MimicShop.Current is not { } shop || !Nodes.Alive(shop.Node)) return;
        var room = NMerchantRoom.Instance;
        if (room is null) return;
        if (ReferenceEquals(__result, room) || ReferenceEquals(__result, room.Inventory))
            __result = shop.Node;
    }
}
