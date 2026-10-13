using Godot;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Rewards;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Shows the picker the game's own selection screens (card reward, choose-a-card, Smith grid)
/// filled with exact copies of the owner's cards. These screens are pure UI in v0.107.1: choosing
/// only completes a task with the result, so nothing is added to the picker's deck or the owner's.
/// </summary>
internal static class NativeScreens
{
    /// <summary>A screen the picker has open, and the option index it resolves to (-1 skip, null closed without choosing).</summary>
    /// <summary>A screen the picker has open, and the option indices it resolves to ([-1] skip, null closed without choosing).</summary>
    public sealed record Open(Node Screen, Task<int[]?> Result);

    public static bool Supports(OfferMsg o) =>
        (o.Kind is DecisionKind.CardReward or DecisionKind.GeneratedCardChoice or DecisionKind.SmithUpgrade or DecisionKind.Bundle
            || DelegationRules.IsDeckChoice(o.Kind))
        && o.Options.Count > 0
        && (o.Kind != DecisionKind.Bundle || o.Packs().Count == o.Groups.Count && o.Groups.Count > 0)
        && o.Options.All(c => !string.IsNullOrEmpty(c.Blob));

    public static Open? Show(OfferMsg o, string ownerName)
    {
        var owner = GameQuery.PlayerById(o.From);
        List<CardModel>? cards;
        if (o.Kind == DecisionKind.SmithUpgrade || DelegationRules.IsDeckChoice(o.Kind))
        {
            // The upgrade preview reads the card's pile and run scope, which only real deck cards have.
            // Every client holds every player's deck, so show the owner's own cards.
            cards = DeckCards(o.Options, owner);
            if (cards is null)
            {
                Log.Warn($"Couldn't match the {o.Kind} options to the owner's deck; using the list instead");
                return null;
            }
        }
        else
        {
            cards = new List<CardModel>(o.Options.Count);
            foreach (var view in o.Options)
            {
                var card = CardCodec.Rebuild(view.Blob, owner);
                if (card is null) return null;
                cards.Add(card);
            }
        }

        switch (o.Kind)
        {
            case DecisionKind.CardReward:
            {
                var options = cards.Select(c => new CardCreationResult(c)).ToList();
                var alternatives = o.CanSkip
                    ? new List<CardRewardAlternative> { new("Skip", PostAlternateCardRewardAction.EndSelectionAndCompleteReward) }
                    : new List<CardRewardAlternative>();
                var screen = NCardRewardSelectionScreen.ShowScreen(options, alternatives);
                if (screen is null) return null;
                var result = RewardResult(screen, cards.Count); // must start before any card can be clicked
                Retitle(screen, $"Choose for {ownerName}");
                return new Open(screen, One(result));
            }

            case DecisionKind.GeneratedCardChoice:
            {
                var screen = NChooseACardSelectionScreen.ShowScreen(cards, o.CanSkip);
                if (screen is null) return null;
                var result = CardsResult(screen.CardsSelected(), cards, o.CanSkip);
                Retitle(screen, $"Choose for {ownerName}");
                return new Open(screen, One(result));
            }

            case DecisionKind.Bundle:
            {
                var packs = new List<IReadOnlyList<CardModel>>();
                int at = 0;
                foreach (int size in o.Groups)
                {
                    packs.Add(cards.GetRange(at, size));
                    at += size;
                }
                var screen = NChooseABundleSelectionScreen.ShowScreen(packs);
                if (screen is null) return null;
                var result = BundleResult(screen, screen.CardsSelected(), packs);
                Retitle(screen, $"Choose a pack for {ownerName}");
                return new Open(screen, One(result));
            }

            case DecisionKind.SmithUpgrade:
            {
                var runState = RunManager.Instance?.DebugOnlyGetState();
                if (runState is null) return null;
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1);
                var screen = NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
                if (screen is null) return null;
                Tag(screen, $"{ownerName}'s deck: upgrade 1 card");
                return new Open(screen, One(CardsResult(screen.CardsSelected(), cards, canSkip: false)));
            }

            // Gifts that remove, transform or upgrade cards (Neow's Precise Scissors / Precarious Shears, New Leaf, Pomander).
            case DecisionKind.DeckRemove:
            {
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.RemoveSelectionPrompt, o.MinSelect, o.MaxSelect);
                var screen = NDeckCardSelectScreen.Create(cards, prefs);
                if (screen is null || NOverlayStack.Instance is not { } stack) return null;
                stack.Push(screen);
                Tag(screen, $"{ownerName}'s deck: remove {Count(o)}");
                return new Open(screen, Many(screen.CardsSelected(), cards));
            }

            case DecisionKind.DeckTransform:
            {
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.TransformSelectionPrompt, o.MinSelect, o.MaxSelect);
                // The game's default: "a random card". Its preview never rolls the RNG.
                var screen = NDeckTransformSelectScreen.ShowScreen(cards, c => new CardTransformation(c), prefs);
                if (screen is null) return null;
                Tag(screen, $"{ownerName}'s deck: transform {Count(o)}");
                return new Open(screen, Many(screen.CardsSelected(), cards));
            }

            case DecisionKind.DeckUpgrade:
            {
                var runState = RunManager.Instance?.DebugOnlyGetState();
                if (runState is null) return null;
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, o.MinSelect, o.MaxSelect);
                var screen = NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
                if (screen is null) return null;
                Tag(screen, $"{ownerName}'s deck: upgrade {Count(o)}");
                return new Open(screen, Many(screen.CardsSelected(), cards));
            }

            default:
                return null;
        }
    }

    /// <summary>The owner's actual deck cards for these options, in the same order, or null if any is missing.</summary>
    private static List<CardModel>? DeckCards(IReadOnlyList<CardView> options, Player? owner)
    {
        var deck = owner?.Deck?.Cards.ToList();
        if (deck is null) return null;
        var used = new HashSet<CardModel>();
        var result = new List<CardModel>(options.Count);
        foreach (var view in options)
        {
            var match = deck.FirstOrDefault(c => !used.Contains(c) && Same(c, view, checkTitle: true))
                     ?? deck.FirstOrDefault(c => !used.Contains(c) && Same(c, view, checkTitle: false));
            if (match is null) return null;
            used.Add(match);
            result.Add(match);
        }
        return result;
    }

    private static bool Same(CardModel card, CardView view, bool checkTitle)
    {
        if ((card.GetType().FullName ?? card.GetType().Name) != view.Id || card.CurrentUpgradeLevel != view.Upgrades) return false;
        return !checkTitle || GameQuery.View(card).Title == view.Title;
    }

    /// <summary>Takes a picker screen down, e.g. when the owner took the choice back.</summary>
    public static void Close(Node screen)
    {
        try
        {
            if (Nodes.Alive(screen) && screen is IOverlayScreen overlay)
                NOverlayStack.Instance?.Remove(overlay);
        }
        catch (Exception ex) { Log.Error("Closing picker screen failed", ex); }
    }

    private static async Task<int[]?> One(Task<int?> result) => await result is { } i ? new[] { i } : null;

    /// <summary>Every selected card's position among the offered cards; null if closed without choosing.</summary>
    private static async Task<int[]?> Many(Task<IEnumerable<CardModel>> selected, List<CardModel> offered)
    {
        var picked = (await selected)?.ToList() ?? new List<CardModel>();
        if (picked.Count == 0) return null;
        var indices = picked.Select(c => offered.FindIndex(o => ReferenceEquals(o, c))).ToArray();
        return indices.All(i => i >= 0) && indices.Distinct().Count() == indices.Length ? indices : null;
    }

    private static async Task<int?> RewardResult(NCardRewardSelectionScreen screen, int cardCount)
    {
        int? chosen = await screen.OptionSelected();
        Close(screen); // the reward screen leaves closing to its caller
        if (chosen is null) return null;
        return chosen.Value >= 0 && chosen.Value < cardCount ? chosen.Value : -1; // past the cards = Skip
    }

    private static async Task<int?> BundleResult(NChooseABundleSelectionScreen screen,
        Task<IEnumerable<IReadOnlyList<CardModel>>> selected, List<IReadOnlyList<CardModel>> packs)
    {
        var picked = (await selected)?.FirstOrDefault();
        Close(screen); // like the reward screen, the pack screen leaves closing to its caller
        if (picked is null || picked.Count == 0) return null;
        int index = packs.FindIndex(p => ReferenceEquals(p, picked) || (p.Count > 0 && ReferenceEquals(p[0], picked[0])));
        return index >= 0 ? index : null;
    }

    private static async Task<int?> CardsResult(Task<IEnumerable<CardModel>> selected, List<CardModel> offered, bool canSkip)
    {
        var picked = (await selected)?.ToList() ?? new List<CardModel>();
        if (picked.Count == 0) return canSkip ? -1 : null;

        var card = picked[0];
        int index = offered.FindIndex(c => ReferenceEquals(c, card));
        if (index < 0) index = offered.FindIndex(c => c.Id.Equals(card.Id) && c.CurrentUpgradeLevel == card.CurrentUpgradeLevel);
        return index >= 0 ? index : null;
    }

    private static string Count(OfferMsg o) => o.MaxSelect > 1 ? $"{o.MaxSelect} cards" : "1 card";

    /// <summary>Deck grids have no banner to retitle, so say whose deck it is in a line across the top.</summary>
    private static void Tag(Node screen, string text)
    {
        try
        {
            var label = Style.Label(text, 26, Style.Accent);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.MouseFilter = Control.MouseFilterEnum.Ignore;
            label.AnchorLeft = 0; label.AnchorRight = 1; label.OffsetTop = 24; label.OffsetBottom = 64;
            label.ZIndex = 10;
            screen.AddChild(label);
        }
        catch (Exception ex) { Log.Warn($"Couldn't label picker screen: {ex.Message}"); }
    }

    /// <summary>"Choose a Card" becomes "Choose for Alex", so it's obvious whose card this is.</summary>
    private static void Retitle(Node screen, string text)
    {
        try { Nodes.FindFirst<NCommonBanner>(screen)?.label?.SetTextAutoSize(text); }
        catch (Exception ex) { Log.Warn($"Couldn't retitle picker screen: {ex.Message}"); }
    }
}

/// <summary>Cards on the wire, using the game's own network serializer so upgrades and enchantments survive.</summary>
internal static class CardCodec
{
    public static string Serialize(CardModel? card)
    {
        if (card is null) return string.Empty;
        try
        {
            var writer = new PacketWriter();
            card.ToSerializable().Serialize(writer);
            writer.ZeroByteRemainder();
            int length = Math.Min(writer.Buffer.Length, Math.Max(writer.BytePosition, (writer.BitPosition + 7) / 8));
            return Convert.ToBase64String(writer.Buffer, 0, length);
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't serialize {card.GetType().Name}: {ex.Message}");
            return string.Empty;
        }
    }

    public static CardModel? Rebuild(string blob, Player? owner)
    {
        try
        {
            var bytes = Convert.FromBase64String(blob);
            var padded = new byte[bytes.Length + 16]; // the bit reader may peek past the last byte
            Buffer.BlockCopy(bytes, 0, padded, 0, bytes.Length);

            var reader = new PacketReader();
            reader.Reset(padded);
            var data = new SerializableCard();
            data.Deserialize(reader);

            var card = CardModel.FromSerializable(data);
            if (owner is not null && card.Owner is null) SetOwner(card, owner);
            return card;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't rebuild a card for the picker screen: {ex.Message}");
            return null;
        }
    }

    // Owner's setter isn't public; the choose-a-card screen reads Owner to show that player's piles in combat.
    private static void SetOwner(CardModel card, Player owner)
    {
        var setter = typeof(CardModel).GetProperty(nameof(CardModel.Owner))?.GetSetMethod(nonPublic: true);
        setter?.Invoke(card, new object[] { owner });
    }
}
