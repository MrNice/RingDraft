using Godot;
using MegaCrit.Sts2.Core.Entities.Merchant;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Lives under NGame for the whole session. Each frame: attach to co-op networking, read
/// incoming messages, hand new owner screens to the picker, and show this player's own
/// picker window when a neighbour needs a choice.
/// </summary>
internal partial class RingDraftController : Node
{
    private sealed class Active
    {
        public required PendingDecision Decision { get; init; }
        public required Node Screen { get; init; }
        public Blocker? Blocker { get; set; }
        public DecisionKind Kind => Decision.Kind;
    }

    private sealed class Job
    {
        public required Func<bool> Step { get; init; }
        public double Next { get; set; }
        public double Until { get; init; }
        public double Interval { get; init; }
    }

    private readonly RingNet _net = new();
    private readonly HashSet<ulong> _handled = new();
    private readonly List<Job> _jobs = new();

    private PickerPanel _panel = null!;
    private ulong _me;
    private OwnerLedger? _ledger;
    private PickerInbox? _inbox;
    private Active? _active;
    private ulong? _shopNodeId;
    private CardView? _pendingRemoval;
    private double _pendingRemovalSince;
    private bool _wasInCombat;
    private double _announceUntil, _nextAnnounce;

    // Picker side: the game screen open for a neighbour's offer, and offers already auto-opened once.
    private (Envelope Offer, NativeScreens.Open Screen)? _native;
    private readonly HashSet<(ulong, uint)> _autoOpened = new();
    private readonly HashSet<ulong> _pickerScreens = new();

    // Everyone picks card rewards at once: a neighbour's card reward opens this player's own first.
    private readonly HashSet<(ulong, uint)> _mirrored = new();
    private readonly HashSet<ulong> _rewardButtonsTried = new();
    private double _holdNativeUntil;
    private (ulong, uint) _smithHoldOffer;
    private double _smithHoldUntil;
    // Picker side: the copy of a neighbour's shop on the game's merchant rug, and the state it last showed.
    private MimicShop? _mimic;
    private ShopStateMsg? _mimicShown;
    private readonly HashSet<(ulong, uint)> _mimicFailed = new();
    // A pick for this player's own screen that arrived while they were still choosing for a neighbour.
    private (PickMsg Pick, ulong Sender)? _deferredPick;

    public override void _Ready()
    {
        Name = "RingDraft";
        ProcessMode = ProcessModeEnum.Always;
        _panel = new PickerPanel();
        AddChild(_panel);
        Log.Info("Ring Draft ready");
    }

    public override void _Process(double delta)
    {
        try { Step(); }
        catch (Exception ex) { Log.Error("Frame step failed", ex); }
    }

    public override void _ExitTree() => Reset();

    private void Step()
    {
        LobbyModes.Step();
        _net.Poll();
        if (!_net.Active)
        {
            if (_me != 0) Reset();
            return;
        }
        double now = SourceTracker.Now();
        if (_me != _net.LocalId)
        {
            Reset();
            _me = _net.LocalId;
            _ledger = new OwnerLedger(_me, GD.Randi() | 1);
            _inbox = new PickerInbox(_me);
            RunModes.Attach(now);
            _announceUntil = now + 30;
            _nextAnnounce = now;
            Log.Info($"Seats: {string.Join(" → ", GameQuery.SeatOrder().Select(_net.NameOf))} → (wraps)");
        }

        // The run's mode, chosen in the lobby. The host repeats it for a while so clients that continue a run agree.
        var mode = RunModes.Effective(_net.IsHost, now);
        if (_net.IsHost && mode is { } announce && now >= _nextAnnounce && now < _announceUntil)
        {
            _net.Send(new ModeMsg(ModeMsg.RunAnnounce, _me, 0, announce));
            _nextAnnounce = now + 5;
        }
        if (mode is not (RunMode.RingDraft or RunMode.RingDraftPvP))
        {
            // Normal run (or a client still hearing which it is): only listen for the host's word.
            SmithHold.Enabled = false;
            DrainModeOnly();
            return;
        }

        if (mode == RunMode.RingDraftPvP)
        {
            Pvp.Announce ??= winner =>
            {
                if (!_net.IsHost) return;
                _net.Send(new PvpEndMsg(0, _me, 0, winner));
                After(1, () => _net.Send(new PvpEndMsg(0, _me, 0, winner)));
            };
            Pvp.Step(now, _net.IsHost);
            if (Pvp.Ended)
            {
                // The match is over: nothing more to pick for anyone.
                if (_active is { } done) ReleaseBlocker(done);
                _active = null;
                CloseNative();
                CloseMimic();
                _panel.HideAll();
                SmithHold.Enabled = false;
                while (_net.TryReceive(out _, out _)) { }
                RunJobs(now);
                return;
            }
        }

        SmithHold.Enabled = Config.Rules.DelegateSmith && Config.Rules.SmithGoesLast;
        SmithHold.Broadcast ??= (floor, waiting) => _net.Send(new RestHoldMsg(floor, _me, 0, waiting));
        SmithHold.NameOf = _net.NameOf;

        TrackContext(now);
        Drain(now);
        RunJobs(now);
        OwnerStep(now);
        PickerStep(now);
    }

    private void Reset()
    {
        SmithHold.Enabled = false;
        SmithHold.Queue.Clear();
        _announceUntil = 0;
        if (_active is { } a) ReleaseBlocker(a);
        _active = null;
        _handled.Clear();
        _jobs.Clear();
        CloseNative();
        CloseMimic();
        _mimicFailed.Clear();
        _autoOpened.Clear();
        _pickerScreens.Clear();
        _mirrored.Clear();
        _rewardButtonsTried.Clear();
        _holdNativeUntil = 0;
        _deferredPick = null;
        _panel?.HideAll();
        _ledger = null;
        _inbox = null;
        _me = 0;
        _shopNodeId = null;
        _pendingRemoval = null;
        _wasInCombat = false;
        SourceTracker.Reset();
    }

    // ---------------------------------------------------------------- context

    private void TrackContext(double now)
    {
        bool inCombat = GameQuery.InCombat();
        if (inCombat && !_wasInCombat) SourceTracker.CombatStart = now;
        _wasInCombat = inCombat;

        if (_pendingRemoval is not null && now - _pendingRemovalSince > 6)
        {
            Log.Warn("Card removal screen never appeared; dropping the picker's removal choice");
            _pendingRemoval = null;
            SendShopState("Card removal didn't go through.");
        }

        // Each time the merchant's inventory is opened again, it is a new shopping trip for the picker.
        if (_shopNodeId is { } sid && GameQuery.ShopInventoryNode() is null)
        {
            _handled.Remove(sid);
            _shopNodeId = null;
        }

        _handled.RemoveWhere(id => GodotObject.InstanceFromId(id) is null);
        _pickerScreens.RemoveWhere(id => GodotObject.InstanceFromId(id) is null);
        _rewardButtonsTried.RemoveWhere(id => GodotObject.InstanceFromId(id) is null);
    }

    // ---------------------------------------------------------------- network

    private void Drain(double now)
    {
        while (_net.TryReceive(out var env, out var sender))
        {
            switch (env)
            {
                case PickMsg p: OnPick(p, sender); break;
                case ShopActionMsg a: OnShopAction(a, sender, now); break;
                case RestHoldMsg h when h.From == sender: SmithHold.Queue.Set(sender, h.RequestId, h.Waiting); break;
                case ModeMsg m when m.RequestId == ModeMsg.RunAnnounce && !_net.IsHost: RunModes.Announced(m.Mode); break;
                case ModeMsg: break;
                case PvpEndMsg e when e.From == sender && !_net.IsHost: Pvp.Heard(e.Winner); break;
                case PvpEndMsg: break;
                default:
                    if (_inbox?.Receive(env, sender) == true)
                        Log.Info($"{env.GetType().Name} #{env.RequestId} from {_net.NameOf(sender)}");
                    break;
            }
        }
    }

    private void DrainModeOnly()
    {
        while (_net.TryReceive(out var env, out _))
            if (env is ModeMsg { RequestId: ModeMsg.RunAnnounce } m && !_net.IsHost) RunModes.Announced(m.Mode);
    }

    // ---------------------------------------------------------------- owner side

    private void OwnerStep(double now)
    {
        if (_ledger is null) return;

        // Screens this player opened while picking for a neighbour are not this player's own decisions.
        var top = GameQuery.TopOverlay();
        bool pickerScreenOnTop = top is not null && _pickerScreens.Contains(top.GetInstanceId());

        // A removal the picker already chose: resolve the grid it opens, no round trip.
        if (_pendingRemoval is not null && !pickerScreenOnTop
            && GameQuery.Classify(GameQuery.TopOverlay(), out var grid, out _) == ScreenKind.DeckGrid
            && grid is not null && !_handled.Contains(grid.GetInstanceId()) && UiDriver.IsReady(grid))
        {
            _handled.Add(grid.GetInstanceId());
            ResolveRemoval(grid);
        }

        if (_active is { } a)
        {
            // Applied once this player has finished choosing for their neighbour and their own screen is back on top.
            if (_deferredPick is { } dp && dp.Pick.RequestId == a.Decision.RequestId)
            {
                if (pickerScreenOnTop && Nodes.Alive(a.Screen)) return;
                _deferredPick = null;
                Apply(a, dp.Pick, dp.Sender);
                return;
            }

            bool stillOpen = Nodes.Alive(a.Screen)
                && (a.Kind != DecisionKind.Shop || GameQuery.ShopInventoryNode() == a.Screen);
            if (stillOpen)
            {
                foreach (var d in _ledger.Tick(now))
                    if (d == a.Decision) a.Blocker?.OfferTakeOver();
                return;
            }
            if (_ledger.Cancel(a.Decision.RequestId))
                _net.Send(new CancelMsg(a.Decision.RequestId, _me, a.Decision.Picker, CancelReason.ScreenClosed));
            ReleaseBlocker(a);
            _active = null;
        }
        _ledger.Prune();
        if (pickerScreenOnTop) return;

        var kindOfScreen = GameQuery.Classify(GameQuery.TopOverlay(), out var screen, out bool isUpgradeGrid, out var gridKind);
        if (screen is null) return;
        ulong id = screen.GetInstanceId();
        if (_handled.Contains(id)) return;

        // The upgrade grid only opens after the owner picks Smith at a rest site.
        string? restOption = isUpgradeGrid ? "SMITH" : null;
        var ctx = new ScreenContext(kindOfScreen, GameQuery.Room(), restOption, SourceTracker.Infer(now), _pendingRemoval is not null, gridKind);
        var kind = DelegationRules.Classify(ctx, Config.Rules);

        if (kind == DecisionKind.ShopRemovalPick) return; // resolved by the block at the top once the grid is ready
        if (kind is null)
        {
            _handled.Add(id);
            if (kindOfScreen == ScreenKind.ShopInventory) _shopNodeId = id;
            return;
        }

        var picker = new RingOrder(GameQuery.SeatOrder(), Config.Rules.ReverseRing).PickerFor(_me);
        if (picker is null) { _handled.Add(id); return; }
        if (kind != DecisionKind.Shop && !UiDriver.IsReady(screen)) return; // try again next frame

        _handled.Add(id);
        if (kind == DecisionKind.Shop) _shopNodeId = id;
        Begin(kind.Value, screen, picker.Value, now, ctx);
    }

    private void Begin(DecisionKind kind, Node screen, ulong picker, double now, ScreenContext ctx)
    {
        int timeout = GameQuery.InCombat() ? Config.Rules.CombatTimeoutSeconds : Config.Rules.TimeoutSeconds;
        string who = _net.NameOf(picker);

        if (kind == DecisionKind.Shop)
        {
            var shop = _ledger!.Open(kind, picker, 0, false, now, timeout);
            _active = new Active { Decision = shop, Screen = screen };
            _active.Blocker = Blocker.Attach(screen, $"{who} is shopping for you, with your gold…", TakeOver);
            SendShopState("");
            Log.Info($"Shop handed to {who} (#{shop.RequestId})");
            return;
        }

        List<CardView> options;
        List<int> groups = new();
        if (kind == DecisionKind.Bundle) (options, groups) = UiDriver.BundleOptions(screen);
        else options = UiDriver.Options(screen);

        int choices = kind == DecisionKind.Bundle ? groups.Count : options.Count;
        if (choices == 0)
        {
            Log.Warn($"{kind} screen had no readable cards; leaving it to you");
            return;
        }
        bool deckChoice = DelegationRules.IsDeckChoice(kind);
        var (min, max) = deckChoice ? UiDriver.Limits(screen) : (1, 1);
        max = Math.Min(max, choices);
        min = Math.Min(min, max);
        bool canSkip = kind != DecisionKind.Bundle && !deckChoice && UiDriver.HasSkip(screen);
        // PvP: one card reward skip every 3 floors, or the pickers would just skip the cards that help you.
        if (canSkip && kind == DecisionKind.CardReward && Pvp.InPvpRun && !Pvp.Skips.CanSkip(Pvp.Floor()))
        {
            canSkip = false;
            Log.Info("No skip left this stretch of floors: your picker has to take a card");
        }
        var decision = _ledger!.Open(kind, picker, choices, canSkip, now, timeout, min, max);

        _net.Send(new OfferMsg(decision.RequestId, _me, picker, kind, options, canSkip,
            GameQuery.MyDeckViews(), GameQuery.MyGold(), GameQuery.MyRelics(), timeout) { Groups = groups, MinSelect = min, MaxSelect = max });

        string cards = max > 1 ? $"which {max} cards" : "which card";
        string waiting = kind switch
        {
            DecisionKind.DeckRemove => $"{who} is choosing {cards} you remove…",
            DecisionKind.DeckTransform => $"{who} is choosing {cards} you transform…",
            DecisionKind.DeckUpgrade => $"{who} is choosing {cards} you upgrade…",
            DecisionKind.Bundle => $"{who} is choosing your pack…",
            DecisionKind.SmithUpgrade => $"{who} is choosing which card you upgrade…",
            DecisionKind.GeneratedCardChoice => $"{who} is choosing your card…",
            _ => $"{who} is picking your card reward…",
        };
        _active = new Active { Decision = decision, Screen = screen };
        _active.Blocker = Blocker.Attach(screen, waiting, TakeOver);
        Log.Info($"{kind} handed to {who} (#{decision.RequestId}, {options.Count} options, source {ctx.Source})");
    }

    private void TakeOver()
    {
        if (_active is not { } a) return;
        if (_ledger!.TakeOver(a.Decision.RequestId))
            _net.Send(new CancelMsg(a.Decision.RequestId, _me, a.Decision.Picker, CancelReason.OwnerTookOver));
        ReleaseBlocker(a);
        _active = null;
        Log.Info($"Took back #{a.Decision.RequestId}");
    }

    private void OnPick(PickMsg p, ulong sender)
    {
        if (_ledger is null) return;
        var verdict = _ledger.TryResolve(p, sender);
        if (verdict != Verdict.Accepted)
        {
            Log.Warn($"Ignored pick #{p.RequestId} from {_net.NameOf(sender)}: {verdict}");
            return;
        }
        if (_active is not { } a || a.Decision.RequestId != p.RequestId) return;

        // This player is still choosing for their neighbour; their own screen is underneath. Apply it once they're done.
        var top = GameQuery.TopOverlay();
        if (top is not null && top != a.Screen && _pickerScreens.Contains(top.GetInstanceId()))
        {
            _deferredPick = (p, sender);
            Log.Info($"{_net.NameOf(sender)}'s pick #{p.RequestId} is in; applying it after you finish your choice");
            return;
        }
        Apply(a, p, sender);
    }

    private void Apply(Active a, PickMsg p, ulong sender)
    {
        ReleaseBlocker(a);
        _active = null;
        if (!Nodes.Alive(a.Screen)) return;

        bool ok = a.Kind == DecisionKind.Bundle ? UiDriver.PressBundle(a.Screen, p.OptionIndex)
                : p.OptionIndex == -1 ? UiDriver.PressSkip(a.Screen)
                : p.Chosen.All(i => UiDriver.PressHolder(a.Screen, i));
        if (ok && a.Kind != DecisionKind.Bundle) WarnIfStillOpen(a.Screen, a.Kind);
        if (ok && a.Kind == DecisionKind.CardReward && p.OptionIndex == -1 && Pvp.InPvpRun)
        {
            Pvp.Skips.Used(Pvp.Floor());
            Log.Info("Skip used; the next one comes back in a few floors");
        }
        if (!ok)
        {
            Log.Warn($"Couldn't apply pick #{p.RequestId} (option {p.OptionIndex}); the screen is yours");
            return;
        }
        if (a.Kind is DecisionKind.SmithUpgrade or DecisionKind.Bundle || DelegationRules.IsDeckChoice(a.Kind)) ConfirmWhenEnabled(a.Screen);
        Log.Info($"Applied {_net.NameOf(sender)}'s {a.Kind} pick: {string.Join(", ", p.Chosen)}");
    }

    private void OnShopAction(ShopActionMsg msg, ulong sender, double now)
    {
        if (_ledger is null) return;
        var entries = GameQuery.ShopEntries(out var inventory);
        var deck = GameQuery.MyDeck();
        var verdict = _ledger.TryShopAction(msg, sender, entries.Select(e => e.View).ToList(), deck.Count);
        if (verdict != Verdict.Accepted)
        {
            Log.Warn($"Ignored shop action {msg.Action} from {_net.NameOf(sender)}: {verdict}");
            if (_active?.Decision.RequestId == msg.RequestId) SendShopState($"That didn't work ({verdict}).");
            return;
        }
        if (_active is not { Kind: DecisionKind.Shop } shop || shop.Decision.RequestId != msg.RequestId) return;

        switch (msg.Action)
        {
            case ShopActionType.Done:
                ReleaseBlocker(shop);
                _active = null;
                // The trip is over for you too: your rug closes, so nothing more gets bought outside the hand-off.
                // Talking to the merchant again starts a new trip for your picker.
                if (Nodes.Alive(shop.Screen) && GameQuery.ShopInventoryNode() == shop.Screen) GameQuery.CloseShop(shop.Screen);
                Log.Info($"{_net.NameOf(sender)} finished shopping for you; your shop is closed");
                break;

            case ShopActionType.Buy:
            {
                var (view, entry) = entries.First(e => e.View.Index == msg.ItemIndex);
                if (inventory is null) SendShopState($"Couldn't buy {view.Title}.");
                else _ = BuyForPicker(entry, inventory, view.Title, msg.RequestId);
                break;
            }

            case ShopActionType.Remove:
            {
                var removal = entries.First(e => e.View.Type == ShopItemType.CardRemoval);
                _pendingRemoval = GameQuery.View(deck[msg.DeckIndex]);
                _pendingRemovalSince = now;
                if (inventory is null || !GameQuery.Purchase(removal.Entry, inventory, "card removal"))
                {
                    _pendingRemoval = null;
                    SendShopState("Card removal didn't go through.");
                }
                break;
            }
        }
    }

    /// <summary>Buys what the picker chose and tells them how it went, including when the game says no.</summary>
    private async Task BuyForPicker(MerchantEntry entry, MerchantInventory inventory, string title, uint requestId)
    {
        var failure = await GameQuery.PurchaseResult(entry, inventory, title);
        if (_active?.Decision.RequestId != requestId) return;
        if (failure is null)
        {
            After(0.6, () => SendShopState($"Bought {title}."));
            return;
        }
        SendShopState(failure switch
        {
            PurchaseStatus.FailureGold => $"Couldn't buy {title}: not enough gold.",
            PurchaseStatus.FailureSpace => $"Couldn't buy {title}: no empty potion slot.",
            PurchaseStatus.FailureOutOfStock => $"Couldn't buy {title}: sold out.",
            _ => $"Couldn't buy {title}.",
        });
    }

    private void ResolveRemoval(Node grid)
    {
        var want = _pendingRemoval!;
        _pendingRemoval = null;
        var options = UiDriver.Options(grid);
        int index = options.FindIndex(o => o.Id == want.Id && o.Upgrades == want.Upgrades && o.Title == want.Title);
        if (index < 0) index = options.FindIndex(o => o.Title == want.Title);
        if (index < 0 || !UiDriver.PressHolder(grid, index))
        {
            Log.Warn($"Couldn't find {want.Title} in the removal grid; it's yours to pick");
            SendShopState($"Couldn't find {want.Title}; they'll pick the removal themselves.");
            return;
        }
        ConfirmWhenEnabled(grid);
        After(1.2, () => SendShopState($"Removed {want.Title}."));
        Log.Info($"Removed {want.Title} as chosen by the picker");
    }

    private void SendShopState(string notice)
    {
        if (_active is not { Kind: DecisionKind.Shop } a) return;
        var entries = GameQuery.ShopEntries(out _);
        int left = (int)Math.Max(0, Math.Ceiling(a.Decision.Deadline - SourceTracker.Now()));
        _net.Send(new ShopStateMsg(a.Decision.RequestId, _me, a.Decision.Picker, GameQuery.MyGold(),
            entries.Select(e => e.View).ToList(), GameQuery.MyDeckViews(), notice, left));
    }

    private static void ReleaseBlocker(Active a)
    {
        if (a.Blocker is { } b && GodotObject.IsInstanceValid(b)) b.Release();
        a.Blocker = null;
    }

    // ---------------------------------------------------------------- picker side

    private void PickerStep(double now)
    {
        var current = _inbox?.Current;

        // The offer behind an open game screen was cancelled or replaced: take the screen down.
        if (_native is { } open && !ReferenceEquals(open.Offer, current)) CloseNative();
        if (_mimic is { } m && (current is not ShopStateMsg cs || cs.From != m.Owner || cs.RequestId != m.RequestId)) CloseMimic();

        if (current is null)
        {
            if (_panel.Shown is not null) _panel.HideAll();
            return;
        }

        if (current is OfferMsg offer && _native is null && ShouldAutoOpen(offer))
        {
            // Both Smithing: our own upgrade screen opens first (and goes to our picker), then the neighbour's on top
            // of it, so each picker chooses for their neighbour at the same time instead of waiting behind a locked screen.
            if (offer.Kind == DecisionKind.SmithUpgrade && _active is null && SmithHold.OwnSmithComing(now))
            {
                if (_smithHoldOffer != (offer.From, offer.RequestId))
                {
                    _smithHoldOffer = (offer.From, offer.RequestId);
                    _smithHoldUntil = now + 4;
                }
                if (now < _smithHoldUntil)
                {
                    _panel.SetSuppressed(true);
                    return;
                }
            }
            // A neighbour opened a card reward: open ours too, and let it reach our picker before we start choosing.
            if (offer.Kind == DecisionKind.CardReward && _mirrored.Add((offer.From, offer.RequestId)) && OpenMyCardRewardToo(now))
                _holdNativeUntil = now + 2.5;
            if (now < _holdNativeUntil && _active is null)
            {
                _panel.SetSuppressed(true);
                return;
            }
            _holdNativeUntil = 0;
            _autoOpened.Add((offer.From, offer.RequestId));
            OpenNative(offer);
        }
        if (current is ShopStateMsg shopState) StepMimic(shopState);
        _panel.SetSuppressed(_native is not null || _mimic is not null);
        if (ReferenceEquals(_panel.Shown, current)) return;

        string owner = _net.NameOf(current.From);
        switch (current)
        {
            case OfferMsg o:
                _panel.ShowOffer(o, owner,
                    chosen => SendPick(o, chosen),
                    NativeScreens.Supports(o) ? () => OpenNative(o) : null);
                break;

            case ShopStateMsg s:
                _panel.ShowShop(s, owner, (action, item, deckIndex) =>
                {
                    _net.Send(new ShopActionMsg(s.RequestId, _me, s.From, action, item, deckIndex));
                    if (action == ShopActionType.Done) _inbox?.Complete(s.From, s.RequestId);
                    else _panel.SetBusy("Waiting for the shop to update…");
                });
                break;
        }
    }

    /// <summary>
    /// The real screen opens by itself, in combat too unless AutoOpenInCombat is off; then it waits for the
    /// picker to press "Show the cards", so a teammate's potion never covers the picker's hand mid-turn.
    /// </summary>
    private bool ShouldAutoOpen(OfferMsg o) =>
        Config.Rules.UseGameScreens
        && NativeScreens.Supports(o)
        && (Config.Rules.AutoOpenInCombat || !GameQuery.InCombat())
        && !_autoOpened.Contains((o.From, o.RequestId));

    /// <summary>Shows the neighbour's shop on the game's own merchant rug, and keeps it in step with each new state.</summary>
    private void StepMimic(ShopStateMsg s)
    {
        if (!Config.Rules.UseGameScreens || _mimicFailed.Contains((s.From, s.RequestId))) return;
        if (_mimic is not null && ReferenceEquals(_mimicShown, s)) return;

        if (_mimic is not null && _mimic.Refresh(s))
        {
            _mimicShown = s;
            return;
        }
        CloseMimic();
        if (!MimicShop.CanOpen()) return;
        OpenMyShopToo(s);

        ulong owner = s.From;
        uint request = s.RequestId;
        int removalIndex = s.Items.FirstOrDefault(i => i.Type == ShopItemType.CardRemoval)?.Index ?? -1;
        var actions = new MimicShop.Actions(
            Buy: index => _net.Send(new ShopActionMsg(request, _me, owner, ShopActionType.Buy, index, -1)),
            Remove: deckIndex => _net.Send(new ShopActionMsg(request, _me, owner, ShopActionType.Remove, removalIndex, deckIndex)),
            Done: () =>
            {
                _mimic = null;
                _mimicShown = null;
                _net.Send(new ShopActionMsg(request, _me, owner, ShopActionType.Done, -1, -1));
                _inbox?.Complete(owner, request);
                Log.Info($"Finished shopping for {_net.NameOf(owner)}");
            },
            PickerScreenOpened: screen => _pickerScreens.Add(screen.GetInstanceId()));

        MimicShop? shop = null;
        try { shop = MimicShop.Open(s, _net.NameOf(owner), actions); }
        catch (Exception ex) { Log.Error("Opening the copy of the shop failed", ex); }
        if (shop is null)
        {
            _mimicFailed.Add((owner, request));
            Log.Warn($"Couldn't show {_net.NameOf(owner)}'s shop on the merchant rug; using the list instead");
            return;
        }
        _mimic = shop;
        _mimicShown = s;
        Log.Info($"Opened {_net.NameOf(owner)}'s shop (#{request})");
    }

    /// <summary>A neighbour opened their shop: open ours too, so it goes to our picker while we shop for them.</summary>
    private void OpenMyShopToo(ShopStateMsg s)
    {
        if (!Config.Rules.OpenAllShops || _active is not null || GameQuery.ShopInventoryNode() is not null) return;
        if (!_mirrored.Add((s.From, s.RequestId))) return;
        var ctx = new ScreenContext(ScreenKind.ShopInventory, GameQuery.Room(), null, ChoiceSource.Unknown, false);
        if (DelegationRules.Classify(ctx, Config.Rules) != DecisionKind.Shop) return;
        if (new RingOrder(GameQuery.SeatOrder(), Config.Rules.ReverseRing).PickerFor(_me) is null) return;
        if (GameQuery.OpenShop()) Log.Info("Opened your shop too, so everyone shops at once");
    }

    private void CloseMimic()
    {
        if (_mimic is not { } m) return;
        _mimic = null;
        _mimicShown = null;
        m.Close();
    }

    private bool OpenMyCardRewardToo(double now)
    {
        if (!Config.Rules.OpenAllCardRewards || _active is not null) return false;
        var ctx = new ScreenContext(ScreenKind.CardReward, GameQuery.Room(), null, SourceTracker.Infer(now), false);
        if (DelegationRules.Classify(ctx, Config.Rules) != DecisionKind.CardReward) return false;
        if (new RingOrder(GameQuery.SeatOrder(), Config.Rules.ReverseRing).PickerFor(_me) is null) return false;
        if (!UiDriver.OpenOwnCardReward(_rewardButtonsTried)) return false;
        Log.Info("Opened your card reward too, so everyone picks at once");
        return true;
    }

    private void OpenNative(OfferMsg o)
    {
        if (_native is not null) return;
        NativeScreens.Open? open = null;
        try { open = NativeScreens.Show(o, _net.NameOf(o.From)); }
        catch (Exception ex) { Log.Error($"Opening the game screen for #{o.RequestId} failed", ex); }

        if (open is null)
        {
            Log.Warn($"Couldn't show the real cards for #{o.RequestId}; using the list instead");
            _panel.SetSuppressed(false);
            return;
        }

        _native = (o, open);
        _pickerScreens.Add(open.Screen.GetInstanceId());
        _panel.SetSuppressed(true);
        _ = AwaitNative(o, open);
        Log.Info($"Opened the game's {o.Kind} screen for {_net.NameOf(o.From)} (#{o.RequestId})");
    }

    private async Task AwaitNative(OfferMsg o, NativeScreens.Open open)
    {
        int[]? index;
        try { index = await open.Result; }
        catch (Exception ex) { Log.Error($"Game screen for #{o.RequestId} failed", ex); index = null; }

        if (_native is not { } still || !ReferenceEquals(still.Offer, o)) return; // closed or superseded meanwhile
        _native = null;
        _panel.SetSuppressed(false);

        if (index is null)
        {
            Log.Info($"Closed the {o.Kind} screen for #{o.RequestId} without choosing; the list is still there");
            return;
        }
        SendPick(o, index);
    }

    private void CloseNative()
    {
        if (_native is not { } open) return;
        _native = null;
        NativeScreens.Close(open.Screen.Screen);
        _panel?.SetSuppressed(false);
    }

    /// <param name="chosen">One option index (or -1 to skip), or several for a multi-card deck choice.</param>
    private void SendPick(OfferMsg o, int[] chosen)
    {
        if (_inbox is null || !_inbox.Has(o.From, o.RequestId))
        {
            Log.Info($"Pick for #{o.RequestId} dropped: the offer was already withdrawn");
            return;
        }
        if (chosen.Length == 0) return;
        var pick = new PickMsg(o.RequestId, _me, o.From, chosen[0]);
        if (chosen.Length > 1) pick = pick with { Indices = chosen };
        _net.Send(pick);
        _inbox?.Complete(o.From, o.RequestId);
        if (_native is { } open && ReferenceEquals(open.Offer, o)) CloseNative();
        Log.Info($"Picked {string.Join(", ", chosen)} for {_net.NameOf(o.From)} (#{o.RequestId})");
    }

    // ---------------------------------------------------------------- small scheduler

    private void After(double seconds, Action action) => _jobs.Add(new Job
    {
        Step = () => { action(); return true; },
        Next = SourceTracker.Now() + seconds,
        Until = SourceTracker.Now() + seconds + 1,
        Interval = 1,
    });

    /// <summary>Grid screens (smith, removal) need a confirm click once the selection registers.</summary>
    private void ConfirmWhenEnabled(Node screen) => _jobs.Add(new Job
    {
        Step = () => !Nodes.Alive(screen) || UiDriver.ClickConfirmIfEnabled(screen),
        Next = SourceTracker.Now() + 0.25,
        Until = SourceTracker.Now() + 6,
        Interval = 0.2,
    });

    /// <summary>Non-grid screens close themselves once a card is taken. If one doesn't, say so in the log.</summary>
    private void WarnIfStillOpen(Node screen, DecisionKind kind)
    {
        if (UiDriver.IsGrid(screen)) return;
        After(4, () =>
        {
            if (Nodes.Alive(screen) && GameQuery.TopOverlay() == screen)
                Log.Warn($"{kind} screen still open 4s after applying the pick; the press may not have registered");
        });
    }

    private void RunJobs(double now)
    {
        for (int i = _jobs.Count - 1; i >= 0; i--)
        {
            var job = _jobs[i];
            if (now < job.Next) continue;
            bool done;
            try { done = job.Step(); }
            catch (Exception ex) { Log.Error("Scheduled step failed", ex); done = true; }
            if (done || now > job.Until) _jobs.RemoveAt(i);
            else job.Next = now + job.Interval;
        }
    }
}
