using Godot;
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
        _net.Poll();
        if (!_net.Active)
        {
            if (_me != 0) Reset();
            return;
        }
        if (_me != _net.LocalId)
        {
            Reset();
            _me = _net.LocalId;
            _ledger = new OwnerLedger(_me, GD.Randi() | 1);
            _inbox = new PickerInbox(_me);
            Log.Info($"Seats: {string.Join(" → ", GameQuery.SeatOrder().Select(_net.NameOf))} → (wraps)");
        }

        double now = SourceTracker.Now();
        TrackContext(now);
        Drain(now);
        RunJobs(now);
        OwnerStep(now);
        PickerStep();
    }

    private void Reset()
    {
        if (_active is { } a) ReleaseBlocker(a);
        _active = null;
        _handled.Clear();
        _jobs.Clear();
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
                default:
                    if (_inbox?.Receive(env, sender) == true)
                        Log.Info($"{env.GetType().Name} #{env.RequestId} from {_net.NameOf(sender)}");
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- owner side

    private void OwnerStep(double now)
    {
        if (_ledger is null) return;

        // A removal the picker already chose: resolve the grid it opens, no round trip.
        if (_pendingRemoval is not null
            && GameQuery.Classify(GameQuery.TopOverlay(), out var grid, out _) == ScreenKind.DeckGrid
            && grid is not null && !_handled.Contains(grid.GetInstanceId()) && UiDriver.IsReady(grid))
        {
            _handled.Add(grid.GetInstanceId());
            ResolveRemoval(grid);
        }

        if (_active is { } a)
        {
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

        var kindOfScreen = GameQuery.Classify(GameQuery.TopOverlay(), out var screen, out bool isUpgradeGrid);
        if (screen is null) return;
        ulong id = screen.GetInstanceId();
        if (_handled.Contains(id)) return;

        // The upgrade grid only opens after the owner picks Smith at a rest site.
        string? restOption = isUpgradeGrid ? "SMITH" : null;
        var ctx = new ScreenContext(kindOfScreen, GameQuery.Room(), restOption, SourceTracker.Infer(now), _pendingRemoval is not null);
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

        var options = UiDriver.Options(screen);
        if (options.Count == 0)
        {
            Log.Warn($"{kind} screen had no readable cards; leaving it to you");
            return;
        }
        bool canSkip = UiDriver.HasSkip(screen);
        var decision = _ledger!.Open(kind, picker, options.Count, canSkip, now, timeout);

        _net.Send(new OfferMsg(decision.RequestId, _me, picker, kind, options, canSkip,
            GameQuery.MyDeckViews(), GameQuery.MyGold(), GameQuery.MyRelics(), timeout));

        string waiting = kind switch
        {
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

        ReleaseBlocker(a);
        _active = null;
        if (!Nodes.Alive(a.Screen)) return;

        bool ok = p.OptionIndex == -1 ? UiDriver.PressSkip(a.Screen) : UiDriver.PressHolder(a.Screen, p.OptionIndex);
        if (ok) WarnIfStillOpen(a.Screen, a.Kind);
        if (!ok)
        {
            Log.Warn($"Couldn't apply pick #{p.RequestId} (option {p.OptionIndex}); the screen is yours");
            return;
        }
        if (a.Kind == DecisionKind.SmithUpgrade) ConfirmWhenEnabled(a.Screen);
        Log.Info($"Applied {_net.NameOf(sender)}'s {a.Kind} pick: option {p.OptionIndex}");
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
                Log.Info($"{_net.NameOf(sender)} finished shopping for you");
                break;

            case ShopActionType.Buy:
            {
                var (view, entry) = entries.First(e => e.View.Index == msg.ItemIndex);
                if (inventory is not null && GameQuery.Purchase(entry, inventory, view.Title)) After(0.6, () => SendShopState($"Bought {view.Title}."));
                else SendShopState($"Couldn't buy {view.Title}.");
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

    private void PickerStep()
    {
        var current = _inbox?.Current;
        if (current is null)
        {
            if (_panel.Shown is not null) _panel.HideAll();
            return;
        }
        if (ReferenceEquals(_panel.Shown, current)) return;

        string owner = _net.NameOf(current.From);
        switch (current)
        {
            case OfferMsg o:
                _panel.ShowOffer(o, owner, index =>
                {
                    _net.Send(new PickMsg(o.RequestId, _me, o.From, index));
                    _inbox?.Complete(o.From, o.RequestId);
                });
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
