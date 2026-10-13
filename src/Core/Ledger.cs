namespace RingDraft.Core;

public enum Verdict
{
    Accepted,
    UnknownRequest,
    WrongSender,
    WrongKind,
    OutOfRange,
    NotAffordable,
    AlreadyClosed,
}

public enum DecisionState { Waiting, Resolved, TakenOver, Cancelled }

public sealed class PendingDecision
{
    public required uint RequestId { get; init; }
    public required DecisionKind Kind { get; init; }
    public required ulong Picker { get; init; }
    public required double Deadline { get; init; }
    public int OptionCount { get; set; }
    public bool CanSkip { get; set; }
    public int MinSelect { get; set; } = 1;
    public int MaxSelect { get; set; } = 1;
    public DecisionState State { get; set; } = DecisionState.Waiting;
    /// <summary>Set once the deadline passes. The picker can still answer until the owner actually takes over.</summary>
    public bool Overdue { get; set; }
}

/// <summary>
/// Owner-side bookkeeping. Validates every incoming pick: right request, right sender,
/// right kind, in range. Nothing from the network touches game state without passing here.
/// </summary>
public sealed class OwnerLedger
{
    private readonly ulong _me;
    private readonly Dictionary<uint, PendingDecision> _open = new();
    private uint _next;

    public OwnerLedger(ulong me, uint seed = 1)
    {
        _me = me;
        _next = seed == 0 ? 1 : seed;
    }

    public IEnumerable<PendingDecision> Waiting => _open.Values.Where(d => d.State == DecisionState.Waiting);

    public PendingDecision Open(DecisionKind kind, ulong picker, int optionCount, bool canSkip, double now, double timeoutSeconds,
        int minSelect = 1, int maxSelect = 1)
    {
        var d = new PendingDecision
        {
            RequestId = _next++,
            Kind = kind,
            Picker = picker,
            Deadline = now + timeoutSeconds,
            OptionCount = optionCount,
            CanSkip = canSkip,
            MinSelect = Math.Max(0, Math.Min(minSelect, maxSelect)),
            MaxSelect = Math.Max(1, maxSelect),
        };
        if (_next == 0) _next = 1;
        _open[d.RequestId] = d;
        return d;
    }

    public PendingDecision? Get(uint requestId) => _open.TryGetValue(requestId, out var d) ? d : null;

    /// <summary>A pick for a card reward, generated choice, or smith grid.</summary>
    public Verdict TryResolve(PickMsg msg, ulong netSender)
    {
        var v = Check(msg, netSender, out var d);
        if (v != Verdict.Accepted) return v;
        if (d!.Kind is DecisionKind.Shop or DecisionKind.ShopRemovalPick) return Verdict.WrongKind;

        if (msg.Indices.Count > 0 || d.MaxSelect > 1)
        {
            var chosen = msg.Chosen;
            if (chosen.Count < Math.Max(1, d.MinSelect) || chosen.Count > d.MaxSelect) return Verdict.OutOfRange;
            if (chosen[0] != msg.OptionIndex || chosen.Distinct().Count() != chosen.Count) return Verdict.OutOfRange;
            if (chosen.Any(i => i < 0 || i >= d.OptionCount)) return Verdict.OutOfRange;
            d.State = DecisionState.Resolved;
            return Verdict.Accepted;
        }

        bool skip = msg.OptionIndex == -1;
        if (skip ? !d.CanSkip : msg.OptionIndex < 0 || msg.OptionIndex >= d.OptionCount)
            return Verdict.OutOfRange;

        d.State = DecisionState.Resolved;
        return Verdict.Accepted;
    }

    /// <summary>
    /// A shop action. Buy/Remove keep the decision open; Done closes it.
    /// <paramref name="items"/> and <paramref name="deckCount"/> describe the shop as it is right now.
    /// </summary>
    public Verdict TryShopAction(ShopActionMsg msg, ulong netSender, IReadOnlyList<ShopItemView> items, int deckCount)
    {
        var v = Check(msg, netSender, out var d);
        if (v != Verdict.Accepted) return v;
        if (d!.Kind != DecisionKind.Shop) return Verdict.WrongKind;

        switch (msg.Action)
        {
            case ShopActionType.Done:
                d.State = DecisionState.Resolved;
                return Verdict.Accepted;

            case ShopActionType.Buy:
            {
                var item = items.FirstOrDefault(i => i.Index == msg.ItemIndex);
                if (item is null || !item.Stocked || item.Type == ShopItemType.CardRemoval) return Verdict.OutOfRange;
                return item.Affordable ? Verdict.Accepted : Verdict.NotAffordable;
            }

            case ShopActionType.Remove:
            {
                var removal = items.FirstOrDefault(i => i.Type == ShopItemType.CardRemoval);
                if (removal is null || !removal.Stocked) return Verdict.OutOfRange;
                if (msg.DeckIndex < 0 || msg.DeckIndex >= deckCount) return Verdict.OutOfRange;
                return removal.Affordable ? Verdict.Accepted : Verdict.NotAffordable;
            }

            default:
                return Verdict.OutOfRange;
        }
    }

    /// <summary>Marks newly overdue decisions and returns them, so the owner can be offered a take-over.</summary>
    public List<PendingDecision> Tick(double now)
    {
        var newlyOverdue = new List<PendingDecision>();
        foreach (var d in Waiting)
        {
            if (!d.Overdue && now >= d.Deadline)
            {
                d.Overdue = true;
                newlyOverdue.Add(d);
            }
        }
        return newlyOverdue;
    }

    public bool TakeOver(uint requestId) => Close(requestId, DecisionState.TakenOver);
    public bool Cancel(uint requestId) => Close(requestId, DecisionState.Cancelled);

    /// <summary>Drop closed decisions so the table doesn't grow over a long run.</summary>
    public void Prune()
    {
        foreach (var id in _open.Where(kv => kv.Value.State != DecisionState.Waiting).Select(kv => kv.Key).ToList())
            _open.Remove(id);
    }

    private bool Close(uint requestId, DecisionState state)
    {
        if (!_open.TryGetValue(requestId, out var d) || d.State != DecisionState.Waiting) return false;
        d.State = state;
        return true;
    }

    private Verdict Check(Envelope msg, ulong netSender, out PendingDecision? d)
    {
        d = null;
        if (msg.To != _me) return Verdict.UnknownRequest;
        if (!_open.TryGetValue(msg.RequestId, out d)) return Verdict.UnknownRequest;
        // The transport tells us who really sent it; the envelope's From must agree, and both must be the assigned picker.
        if (msg.From != netSender || netSender != d.Picker) return Verdict.WrongSender;
        if (d.State != DecisionState.Waiting) return Verdict.AlreadyClosed;
        return Verdict.Accepted;
    }
}

/// <summary>
/// Picker-side queue of things to choose. Usually one at a time, but if a seat drops out
/// the ring can route two players to the same picker.
/// </summary>
public sealed class PickerInbox
{
    private readonly ulong _me;
    private readonly List<Envelope> _items = new();

    public PickerInbox(ulong me) => _me = me;

    public Envelope? Current => _items.Count > 0 ? _items[0] : null;
    public int Count => _items.Count;

    /// <summary>Returns true if the message was for us and changed the queue.</summary>
    public bool Receive(Envelope e, ulong netSender)
    {
        if (e.To != _me || e.From != netSender) return false;

        int existing = _items.FindIndex(x => x.From == e.From && x.RequestId == e.RequestId);
        switch (e)
        {
            case OfferMsg:
                if (existing >= 0) return false;
                _items.Add(e);
                return true;
            case ShopStateMsg:
                if (existing >= 0) _items[existing] = e; // refreshed after a purchase
                else _items.Add(e);
                return true;
            case CancelMsg:
                if (existing < 0) return false;
                _items.RemoveAt(existing);
                return true;
            default:
                return false; // picks and actions travel the other way
        }
    }

    public bool Has(ulong owner, uint requestId) => _items.Any(x => x.From == owner && x.RequestId == requestId);

    /// <summary>Called once the picker has answered (or pressed Done in a shop).</summary>
    public void Complete(ulong owner, uint requestId) =>
        _items.RemoveAll(x => x.From == owner && x.RequestId == requestId);
}
