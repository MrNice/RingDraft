using RingDraft.Core;

int failures = 0, passes = 0;
void Check(bool ok, string name)
{
    if (ok) passes++;
    else { failures++; Console.WriteLine($"FAIL  {name}"); }
}

const ulong A = 101, B = 202, C = 303, D = 404;

// ---------- Ring ----------
{
    var two = new RingOrder(new[] { A, B });
    Check(two.PickerFor(A) == B && two.PickerFor(B) == A, "2 players swap");

    var four = new RingOrder(new[] { A, B, C, D });
    Check(four.PickerFor(B) == A, "A picks for B");
    Check(four.PickerFor(C) == B, "B picks for C");
    Check(four.PickerFor(A) == D, "last seat wraps to pick for first");
    Check(four.TargetOf(D) == A && four.TargetOf(A) == B, "TargetOf is inverse of PickerFor");
    foreach (var p in four.Seats)
        Check(four.PickerFor(four.TargetOf(p)!.Value) == p, $"round trip for seat {p}");

    var gone = new HashSet<ulong> { A };
    Check(four.PickerFor(B, id => !gone.Contains(id)) == D, "disconnected picker is skipped backwards");
    gone = new HashSet<ulong> { A, C, D };
    Check(four.PickerFor(B, id => !gone.Contains(id)) is null, "everyone else gone: owner keeps choice");

    Check(new RingOrder(new[] { A }).PickerFor(A) is null, "solo: no picker");
    Check(four.PickerFor(999) is null, "unknown player: no picker");

    var rev = new RingOrder(new[] { A, B, C }, reverse: true);
    Check(rev.PickerFor(A) == B && rev.PickerFor(B) == C && rev.PickerFor(C) == A, "reverse ring");

    var dup = new RingOrder(new[] { A, B, A, C });
    Check(dup.Count == 3, "duplicate seats collapse");
}

// ---------- Rules ----------
{
    var cfg = new RulesConfig();
    DecisionKind? K(ScreenKind s, RoomKind r, string? rest = null, ChoiceSource src = ChoiceSource.Unknown, bool removal = false,
        DeckGridKind grid = DeckGridKind.Other) =>
        DelegationRules.Classify(new ScreenContext(s, r, rest, src, removal, grid), cfg);

    Check(K(ScreenKind.CardReward, RoomKind.Combat) == DecisionKind.CardReward, "combat card reward delegated");
    Check(K(ScreenKind.CardReward, RoomKind.Other) == DecisionKind.CardReward, "post-combat card reward delegated");
    Check(K(ScreenKind.CardReward, RoomKind.Event) is null, "event card reward stays with owner by default");

    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Combat, src: ChoiceSource.Potion) == DecisionKind.GeneratedCardChoice, "Attack/Skill/Power potion delegated");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Combat, src: ChoiceSource.Relic) == DecisionKind.GeneratedCardChoice, "Toolbox delegated");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Combat, src: ChoiceSource.Card) is null, "card-effect choice stays with owner by default");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Combat) == DecisionKind.GeneratedCardChoice, "unknown combat choice delegated by default");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Event, src: ChoiceSource.Potion) is null, "generated choice outside combat (event) stays");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Ancient) == DecisionKind.GeneratedCardChoice, "Hefty Tablet at Neow delegated");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Merchant) == DecisionKind.GeneratedCardChoice, "Massive Scroll bought at shop delegated");
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Treasure) == DecisionKind.GeneratedCardChoice, "card-choice relic from chest delegated");
    Check(K(ScreenKind.CardReward, RoomKind.Ancient) == DecisionKind.CardReward, "Lost Coffer / Orrery card reward at Neow delegated");
    Check(K(ScreenKind.Bundle, RoomKind.Ancient) == DecisionKind.Bundle, "Scroll Boxes packs at Neow delegated");
    Check(K(ScreenKind.Bundle, RoomKind.Merchant) == DecisionKind.Bundle, "Scroll Boxes bought at shop delegated");
    Check(K(ScreenKind.Bundle, RoomKind.Event) is null, "packs inside a regular event stay");
    Check(K(ScreenKind.DeckGrid, RoomKind.Ancient) is null, "unknown deck grid at Neow stays with owner");
    Check(K(ScreenKind.DeckGrid, RoomKind.Ancient, grid: DeckGridKind.Remove) == DecisionKind.DeckRemove, "Neow's remove-a-card delegated");
    Check(K(ScreenKind.DeckGrid, RoomKind.Ancient, grid: DeckGridKind.Transform) == DecisionKind.DeckTransform, "Neow's transform delegated");
    Check(K(ScreenKind.DeckGrid, RoomKind.Ancient, grid: DeckGridKind.Upgrade) == DecisionKind.DeckUpgrade, "Pomander's upgrade delegated");
    Check(K(ScreenKind.DeckGrid, RoomKind.Treasure, grid: DeckGridKind.Remove) == DecisionKind.DeckRemove, "removal relic from a chest delegated");
    Check(K(ScreenKind.DeckGrid, RoomKind.Event, grid: DeckGridKind.Transform) is null, "event transform stays with owner");
    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: "COOK", grid: DeckGridKind.Remove) is null, "rest-site removal stays with owner");
    Check(K(ScreenKind.DeckGrid, RoomKind.Merchant, grid: DeckGridKind.Remove) is null, "merchant removal grid only via the picker's purchase");
    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: "SMITH", grid: DeckGridKind.Upgrade) == DecisionKind.SmithUpgrade, "smith keeps its own kind");

    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: "SMITH") == DecisionKind.SmithUpgrade, "smith grid delegated after owner picks smith");
    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: "smith_option") == DecisionKind.SmithUpgrade, "smith id match is case-insensitive substring");
    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: "REST") is null, "non-smith rest grid stays");
    Check(K(ScreenKind.DeckGrid, RoomKind.RestSite, rest: null) is null, "rest grid with unknown option stays");
    Check(K(ScreenKind.DeckGrid, RoomKind.Event) is null, "event deck grid (transform etc.) stays");
    Check(K(ScreenKind.DeckGrid, RoomKind.Merchant, removal: true) == DecisionKind.ShopRemovalPick, "shop removal grid resolved from picker's choice");
    Check(K(ScreenKind.DeckGrid, RoomKind.Merchant, removal: false) is null, "unexpected merchant grid stays");

    Check(K(ScreenKind.ShopInventory, RoomKind.Merchant) == DecisionKind.Shop, "shop delegated");
    Check(K(ScreenKind.Other, RoomKind.Treasure) is null, "treasure chest stays");
    Check(K(ScreenKind.None, RoomKind.Combat) is null, "plain combat stays");

    cfg.DelegateAncientRewards = false;
    Check(K(ScreenKind.CardReward, RoomKind.Ancient) is null && K(ScreenKind.Bundle, RoomKind.Ancient) is null
          && K(ScreenKind.GeneratedCardChoice, RoomKind.Ancient) is null
          && K(ScreenKind.DeckGrid, RoomKind.Ancient, grid: DeckGridKind.Remove) is null, "Ancient toggle off keeps all Ancient gifts");
    Check(K(ScreenKind.Bundle, RoomKind.Merchant) == DecisionKind.Bundle, "Ancient toggle doesn't affect shop relics");
    cfg.DelegateAncientRewards = true;
    cfg.DelegateCardEffectChoices = true;
    Check(K(ScreenKind.GeneratedCardChoice, RoomKind.Combat, src: ChoiceSource.Card) == DecisionKind.GeneratedCardChoice, "card-effect toggle works");
    cfg.DelegateShop = false;
    Check(K(ScreenKind.ShopInventory, RoomKind.Merchant) is null, "shop toggle works");

    Check(DelegationRules.InferSource(10, lastPotionUse: 9.5, lastCardPlay: null, combatStart: 0) == ChoiceSource.Potion, "recent potion");
    Check(DelegationRules.InferSource(10, lastPotionUse: 8, lastCardPlay: 9.5, combatStart: 0) == ChoiceSource.Card, "most recent wins (card)");
    Check(DelegationRules.InferSource(10, lastPotionUse: 9.8, lastCardPlay: 9.0, combatStart: 0) == ChoiceSource.Potion, "most recent wins (potion)");
    Check(DelegationRules.InferSource(1, null, null, combatStart: 0) == ChoiceSource.Relic, "combat start = relic");
    Check(DelegationRules.InferSource(10, lastPotionUse: 1, lastCardPlay: 2, combatStart: 0) == ChoiceSource.Unknown, "stale events = unknown");
}

// ---------- Codec ----------
{
    var deck = new List<CardView> { new("MegaCrit.Sts2.Cards.Strike", "Strike", "Deal 6 damage.", 0, "AQIDBA=="), new("MegaCrit.Sts2.Cards.Bash", "Bash+", "Deal 10 damage. Apply 3 Vulnerable.", 1) };
    var msgs = new Envelope[]
    {
        new OfferMsg(7, A, B, DecisionKind.CardReward, deck, true, deck, 123, new List<string> { "Burning Blood" }, 90),
        new OfferMsg(8, A, B, DecisionKind.SmithUpgrade, new List<CardView>(), false, new List<CardView>(), 0, new List<string>(), 30),
        new PickMsg(7, B, A, -1),
        new ShopStateMsg(9, A, B, 250, new List<ShopItemView>
        {
            new(0, ShopItemType.Card, "x", "Pommel Strike", "Deal 9.", 50, true),
            new(5, ShopItemType.CardRemoval, "", "Remove a card", "", 75, true),
            new(2, ShopItemType.Card, "y", "Shrug It Off", "Gain 8 Block.", 37, true, Stocked: true, BaseCost: 75, OnSale: true, Blob: "AQID"),
            new(3, ShopItemType.Relic, "", "", "", 0, false, Stocked: false),
        }, deck, "Bought Pommel Strike", 90),
        new ShopActionMsg(9, B, A, ShopActionType.Remove, 5, 1),
        new CancelMsg(9, A, B, CancelReason.OwnerTookOver),
    };

    foreach (var m in msgs)
    {
        var bytes = Codec.Encode(m);
        bool ok = Codec.TryDecode(bytes, out var back);
        Check(ok && back is not null && back.GetType() == m.GetType(), $"decode {m.GetType().Name}");
        Check(ok && Codec.Encode(back!).AsSpan().SequenceEqual(bytes), $"re-encode identical {m.GetType().Name}");
        Check(Codec.TryFromBase64(Codec.ToBase64(m), out var b64) && b64 is not null && Codec.Encode(b64).AsSpan().SequenceEqual(bytes), $"base64 {m.GetType().Name}");

        // Every truncation must fail cleanly, never throw.
        bool allTruncFail = true;
        for (int len = 0; len < bytes.Length; len++)
            if (Codec.TryDecode(bytes[..len], out _)) allTruncFail = false;
        Check(allTruncFail, $"all truncations rejected {m.GetType().Name}");

        Check(!Codec.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), out _), $"trailing byte rejected {m.GetType().Name}");
    }

    Codec.TryDecode(Codec.Encode(msgs[3]), out var shopBack);
    var shopItems = ((ShopStateMsg)shopBack!).Items;
    Check(shopItems[2] == new ShopItemView(2, ShopItemType.Card, "y", "Shrug It Off", "Gain 8 Block.", 37, true, true, 75, true, "AQID"),
        "shop slot keeps stock, base cost, sale and card");
    Check(!shopItems[3].Stocked && shopItems[0].Stocked, "sold-out slot round-trips");

    var o = (OfferMsg)msgs[0];
    Codec.TryDecode(Codec.Encode(o), out var e0);
    var o2 = (OfferMsg)e0!;
    Check(o2.Options[1] == o.Options[1] && o2.Gold == 123 && o2.Relics[0] == "Burning Blood" && o2.CanSkip, "offer fields survive");
    Check(o2.Options[0].Blob == "AQIDBA==" && o2.Options[1].Blob == "", "card blobs survive (present and absent)");
    var huge = new OfferMsg(1, A, B, DecisionKind.CardReward, new List<CardView> { deck[0] with { Blob = new string('A', 5000) } }, false, new List<CardView>(), 0, new List<string>(), 1);
    Check(Codec.TryDecode(Codec.Encode(huge), out var hugeBack) && ((OfferMsg)hugeBack!).Options[0].Blob == "", "oversized blob dropped on encode, not sent");

    var bundle = new OfferMsg(11, A, B, DecisionKind.Bundle, deck.Concat(deck).Concat(deck).ToList(), false, deck, 50, new List<string>(), 90) { Groups = new[] { 2, 2, 2 } };
    Check(Codec.TryDecode(Codec.Encode(bundle), out var bb) && bb is OfferMsg ob && ob.Groups.SequenceEqual(new[] { 2, 2, 2 })
          && ob.ChoiceCount == 3 && ob.Packs().Count == 3 && ob.Packs()[2][1].Title == "Bash+", "bundle packs survive round trip");
    var shears = new OfferMsg(12, A, B, DecisionKind.DeckRemove, deck, false, deck, 0, new List<string>(), 90) { MinSelect = 2, MaxSelect = 2 };
    Check(Codec.TryDecode(Codec.Encode(shears), out var sb) && sb is OfferMsg os && os.MinSelect == 2 && os.MaxSelect == 2 && os.Kind == DecisionKind.DeckRemove,
        "select counts survive round trip");
    Check(!Codec.TryDecode(Codec.Encode(shears with { MinSelect = 3, MaxSelect = 2 }), out _), "min above max rejected");
    var multi = new PickMsg(12, B, A, 1) { Indices = new[] { 1, 0 } };
    Check(Codec.TryDecode(Codec.Encode(multi), out var mb) && mb is PickMsg pm && pm.Chosen.SequenceEqual(new[] { 1, 0 }), "multi-pick round trip");
    Check(Codec.TryDecode(Codec.Encode(msgs[2]), out var sp) && ((PickMsg)sp!).Chosen.SequenceEqual(new[] { -1 }), "single pick reads as one choice");
    var badGroups = bundle with { Groups = new[] { 5, 5 } };
    Check(!Codec.TryDecode(Codec.Encode(badGroups), out _), "groups claiming more cards than sent are rejected");
    Check(((OfferMsg)msgs[0]).ChoiceCount == 2 && ((OfferMsg)msgs[0]).Groups.Count == 0, "non-bundle offers have no groups");

    var wrongVersion = Codec.Encode(msgs[2]); wrongVersion[0] = 99;
    Check(!Codec.TryDecode(wrongVersion, out _), "version mismatch rejected");
    var badTag = Codec.Encode(msgs[2]); badTag[1] = 77;
    Check(!Codec.TryDecode(badTag, out _), "unknown tag rejected");
    Check(!Codec.TryFromBase64("not base64!!", out _), "garbage base64 rejected");
    Check(!Codec.TryFromBase64(null, out _), "null rejected");

    var rng = new Random(1234);
    bool fuzzOk = true;
    for (int i = 0; i < 20000; i++)
    {
        var junk = new byte[rng.Next(0, 64)];
        rng.NextBytes(junk);
        if (junk.Length > 0 && rng.Next(2) == 0) junk[0] = Codec.Version;
        try { Codec.TryDecode(junk, out _); } catch { fuzzOk = false; break; }
    }
    Check(fuzzOk, "random bytes never throw");

    bool tooLong = false;
    try { Codec.Encode(new OfferMsg(1, A, B, DecisionKind.CardReward, Enumerable.Repeat(deck[0], 600).ToList(), false, new List<CardView>(), 0, new List<string>(), 1)); }
    catch (ArgumentException) { tooLong = true; }
    Check(tooLong, "oversized list refused on encode");
}

// ---------- Owner ledger ----------
{
    var ledger = new OwnerLedger(me: B, seed: 5);
    var d = ledger.Open(DecisionKind.CardReward, picker: A, optionCount: 3, canSkip: true, now: 0, timeoutSeconds: 90);
    Check(d.RequestId == 5, "seeded request ids");

    Check(ledger.TryResolve(new PickMsg(d.RequestId, C, B, 0), C) == Verdict.WrongSender, "non-picker rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, B, 0), C) == Verdict.WrongSender, "spoofed From rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId + 1, A, B, 0), A) == Verdict.UnknownRequest, "unknown request rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, C, 0), A) == Verdict.UnknownRequest, "addressed to someone else rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, B, 3), A) == Verdict.OutOfRange, "index past end rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, B, -2), A) == Verdict.OutOfRange, "negative index rejected");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, B, 2), A) == Verdict.Accepted, "valid pick accepted");
    Check(ledger.TryResolve(new PickMsg(d.RequestId, A, B, 1), A) == Verdict.AlreadyClosed, "duplicate pick rejected");

    var cut = ledger.Open(DecisionKind.DeckRemove, A, optionCount: 10, canSkip: false, now: 0, timeoutSeconds: 90, minSelect: 2, maxSelect: 2);
    PickMsg Cut(params int[] idx) => new(cut.RequestId, A, B, idx.Length > 0 ? idx[0] : 0) { Indices = idx };
    Check(ledger.TryResolve(new PickMsg(cut.RequestId, A, B, 3), A) == Verdict.OutOfRange, "two-card removal needs two cards");
    Check(ledger.TryResolve(Cut(3, 3), A) == Verdict.OutOfRange, "same card twice rejected");
    Check(ledger.TryResolve(Cut(3, 10), A) == Verdict.OutOfRange, "second card out of range rejected");
    Check(ledger.TryResolve(Cut(1, 2, 3), A) == Verdict.OutOfRange, "too many cards rejected");
    Check(ledger.TryResolve(new PickMsg(cut.RequestId, A, B, 5) { Indices = new[] { 3, 4 } }, A) == Verdict.OutOfRange, "OptionIndex must match the first choice");
    Check(ledger.TryResolve(Cut(3, 7), A) == Verdict.Accepted, "two distinct cards accepted");
    var one = ledger.Open(DecisionKind.DeckTransform, A, 5, false, 0, 90);
    Check(ledger.TryResolve(new PickMsg(one.RequestId, A, B, 0) { Indices = new[] { 0, 1 } }, A) == Verdict.OutOfRange, "extra picks on a single choice rejected");
    Check(ledger.TryResolve(new PickMsg(one.RequestId, A, B, 4), A) == Verdict.Accepted, "single transform accepted");

    var smith = ledger.Open(DecisionKind.SmithUpgrade, A, optionCount: 10, canSkip: false, now: 0, timeoutSeconds: 90);
    Check(ledger.TryResolve(new PickMsg(smith.RequestId, A, B, -1), A) == Verdict.OutOfRange, "skip refused when not allowed");
    Check(ledger.Cancel(smith.RequestId), "cancel open decision");

    var potion = ledger.Open(DecisionKind.GeneratedCardChoice, A, 3, false, now: 100, timeoutSeconds: 30);
    Check(ledger.Tick(129).Count == 0, "not overdue before deadline");
    var od = ledger.Tick(130);
    Check(od.Count == 1 && od[0] == potion && potion.Overdue, "overdue at deadline");
    Check(ledger.Tick(200).Count == 0, "overdue reported once");
    Check(ledger.TryResolve(new PickMsg(potion.RequestId, A, B, 1), A) == Verdict.Accepted, "late pick still lands before take-over");

    var taken = ledger.Open(DecisionKind.CardReward, A, 3, true, 0, 1);
    Check(ledger.TakeOver(taken.RequestId), "take over");
    Check(ledger.TryResolve(new PickMsg(taken.RequestId, A, B, 0), A) == Verdict.AlreadyClosed, "pick after take-over rejected");
    Check(!ledger.TakeOver(taken.RequestId), "take over twice is a no-op");

    var shop = ledger.Open(DecisionKind.Shop, A, 0, false, 0, 90);
    var items = new List<ShopItemView>
    {
        new(0, ShopItemType.Card, "c", "Card", "", 50, true),
        new(1, ShopItemType.Relic, "r", "Relic", "", 300, false),
        new(9, ShopItemType.CardRemoval, "", "Remove", "", 75, true),
    };
    ShopActionMsg Act(ShopActionType t, int item = 0, int deckIdx = 0) => new(shop.RequestId, A, B, t, item, deckIdx);

    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 0), A, items, 12) == Verdict.Accepted, "buy affordable");
    Check(shop.State == DecisionState.Waiting, "shop stays open after buy");
    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 1), A, items, 12) == Verdict.NotAffordable, "can't overspend target's gold");
    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 4), A, items, 12) == Verdict.OutOfRange, "missing item");
    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 9), A, items, 12) == Verdict.OutOfRange, "removal can't be bought as an item");
    Check(ledger.TryShopAction(Act(ShopActionType.Remove, 0, 11), A, items, 12) == Verdict.Accepted, "remove valid deck card");
    Check(ledger.TryShopAction(Act(ShopActionType.Remove, 0, 12), A, items, 12) == Verdict.OutOfRange, "remove out of deck range");
    var soldOut = new List<ShopItemView> { items[0] with { Stocked = false }, items[1], items[2] with { Stocked = false } };
    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 0), A, soldOut, 12) == Verdict.OutOfRange, "sold-out slot can't be bought");
    Check(ledger.TryShopAction(Act(ShopActionType.Remove, 0, 1), A, soldOut, 12) == Verdict.OutOfRange, "used removal can't be bought again");
    var noRemoval = items.Take(2).ToList();
    Check(ledger.TryShopAction(Act(ShopActionType.Remove, 0, 1), A, noRemoval, 12) == Verdict.OutOfRange, "removal already used");
    Check(ledger.TryResolve(new PickMsg(shop.RequestId, A, B, 0), A) == Verdict.WrongKind, "pick msg can't drive the shop");
    Check(ledger.TryShopAction(Act(ShopActionType.Done), C, items, 12) == Verdict.WrongSender, "only picker can finish shop");
    Check(ledger.TryShopAction(Act(ShopActionType.Done), A, items, 12) == Verdict.Accepted && shop.State == DecisionState.Resolved, "done closes shop");
    Check(ledger.TryShopAction(Act(ShopActionType.Buy, 0), A, items, 12) == Verdict.AlreadyClosed, "no buying after done");

    var cardDecision = ledger.Open(DecisionKind.CardReward, A, 3, true, 0, 90);
    Check(ledger.TryShopAction(new ShopActionMsg(cardDecision.RequestId, A, B, ShopActionType.Done, 0, 0), A, items, 1) == Verdict.WrongKind, "shop msg can't drive a card reward");

    ledger.Prune();
    Check(ledger.Get(d.RequestId) is null && ledger.Get(cardDecision.RequestId) is not null, "prune drops closed only");
}

// ---------- Picker inbox ----------
{
    var inbox = new PickerInbox(me: A);
    var offer = new OfferMsg(1, B, A, DecisionKind.CardReward, new List<CardView>(), true, new List<CardView>(), 0, new List<string>(), 90);
    Check(!inbox.Receive(offer with { To = C }, B), "not for me ignored");
    Check(!inbox.Receive(offer, C), "spoofed owner ignored");
    Check(inbox.Receive(offer, B) && inbox.Count == 1, "offer queued");
    Check(inbox.Has(B, 1) && !inbox.Has(B, 2) && !inbox.Has(C, 1), "Has matches owner and request");
    Check(!inbox.Receive(offer, B) && inbox.Count == 1, "duplicate offer ignored");

    var offer2 = offer with { From = D, RequestId = 1 };
    Check(inbox.Receive(offer2, D) && inbox.Count == 2 && inbox.Current == offer, "second owner queues FIFO");

    Check(inbox.Receive(new CancelMsg(1, B, A, CancelReason.OwnerTookOver), B) && inbox.Current == offer2, "cancel removes the right one");
    Check(!inbox.Receive(new CancelMsg(42, B, A, CancelReason.ScreenClosed), B), "cancel for unknown ignored");

    var s1 = new ShopStateMsg(3, D, A, 100, new List<ShopItemView>(), new List<CardView>(), "", 90);
    var s2 = s1 with { Gold = 50, Notice = "bought" };
    inbox.Receive(s1, D);
    inbox.Receive(s2, D);
    Check(inbox.Count == 2, "shop state refresh replaces in place");
    inbox.Complete(D, 1);
    Check(inbox.Current is ShopStateMsg s && s.Gold == 50, "refreshed shop state is current");
    Check(!inbox.Receive(new PickMsg(1, B, A, 0), B), "picks never enter the inbox");
}

Console.WriteLine($"{passes} passed, {failures} failed");
return failures == 0 ? 0 : 1;
