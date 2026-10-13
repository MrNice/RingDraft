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
    public sealed record Open(Node Screen, Task<int?> Result);

    public static bool Supports(OfferMsg o) =>
        o.Kind is DecisionKind.CardReward or DecisionKind.GeneratedCardChoice or DecisionKind.SmithUpgrade
        && o.Options.Count > 0
        && o.Options.All(c => !string.IsNullOrEmpty(c.Blob));

    public static Open? Show(OfferMsg o, string ownerName)
    {
        var owner = GameQuery.PlayerById(o.From);
        var cards = new List<CardModel>(o.Options.Count);
        foreach (var view in o.Options)
        {
            var card = CardCodec.Rebuild(view.Blob, owner);
            if (card is null) return null;
            cards.Add(card);
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
                return new Open(screen, result);
            }

            case DecisionKind.GeneratedCardChoice:
            {
                var screen = NChooseACardSelectionScreen.ShowScreen(cards, o.CanSkip);
                if (screen is null) return null;
                var result = CardsResult(screen.CardsSelected(), cards, o.CanSkip);
                Retitle(screen, $"Choose for {ownerName}");
                return new Open(screen, result);
            }

            case DecisionKind.SmithUpgrade:
            {
                var runState = RunManager.Instance?.DebugOnlyGetState();
                if (runState is null) return null;
                var prefs = new CardSelectorPrefs(CardSelectorPrefs.UpgradeSelectionPrompt, 1);
                var screen = NDeckUpgradeSelectScreen.ShowScreen(cards, prefs, runState);
                if (screen is null) return null;
                return new Open(screen, CardsResult(screen.CardsSelected(), cards, canSkip: false));
            }

            default:
                return null;
        }
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

    private static async Task<int?> RewardResult(NCardRewardSelectionScreen screen, int cardCount)
    {
        int? chosen = await screen.OptionSelected();
        Close(screen); // the reward screen leaves closing to its caller
        if (chosen is null) return null;
        return chosen.Value >= 0 && chosen.Value < cardCount ? chosen.Value : -1; // past the cards = Skip
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
