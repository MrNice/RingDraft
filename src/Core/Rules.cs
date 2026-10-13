namespace RingDraft.Core;

/// <summary>What kind of screen is on top, as the game layer sees it.</summary>
public enum ScreenKind
{
    None,
    /// <summary>The "pick 1 of 3 cards" screen opened from a card reward.</summary>
    CardReward,
    /// <summary>A non-grid "pick one of these generated cards" prompt (potions, Toolbox-style relics, Discovery-style cards).</summary>
    GeneratedCardChoice,
    /// <summary>A grid of cards from your own deck (smith upgrade, shop removal, event transforms...).</summary>
    DeckGrid,
    /// <summary>The merchant's inventory is open.</summary>
    ShopInventory,
    Other,
}

public enum RoomKind { Other, Combat, Merchant, RestSite, Event, Treasure }

/// <summary>What caused a generated card choice in combat.</summary>
public enum ChoiceSource { Unknown, Potion, Relic, Card }

public enum DecisionKind : byte
{
    CardReward = 1,
    GeneratedCardChoice = 2,
    SmithUpgrade = 3,
    Shop = 4,
    /// <summary>The removal grid opened by a shop removal the picker already chose. Resolved locally, no network round trip.</summary>
    ShopRemovalPick = 5,
}

public sealed class RulesConfig
{
    public bool DelegateCardRewards { get; set; } = true;
    /// <summary>Card reward screens that come out of an event. Off by default: events stay with their owner.</summary>
    public bool DelegateEventCardRewards { get; set; } = false;
    public bool DelegateShop { get; set; } = true;
    public bool DelegateSmith { get; set; } = true;
    public bool DelegatePotionChoices { get; set; } = true;
    public bool DelegateRelicChoices { get; set; } = true;
    /// <summary>Choices created by playing a card (Discovery-style). Off by default: playing cards is the owner's turn.</summary>
    public bool DelegateCardEffectChoices { get; set; } = false;
    /// <summary>In-combat generated choices whose source could not be determined.</summary>
    public bool DelegateUnknownCombatChoices { get; set; } = true;
    /// <summary>Show the picker the game's own card screens (real art) instead of the text list.</summary>
    public bool UseGameScreens { get; set; } = true;
    /// <summary>Picker clockwise vs counter-clockwise.</summary>
    public bool ReverseRing { get; set; } = false;
    /// <summary>After this long the owner gets a "choose myself" button.</summary>
    public int TimeoutSeconds { get; set; } = 90;
    public int CombatTimeoutSeconds { get; set; } = 30;
}

public readonly record struct ScreenContext(
    ScreenKind Screen,
    RoomKind Room,
    string? LastRestOption,
    ChoiceSource Source,
    bool ShopRemovalPending);

public static class DelegationRules
{
    /// <summary>
    /// Returns the decision to hand to the picker, or null if the owner keeps it.
    /// Never delegated: event options, chest and elite relics, potion and gold rewards,
    /// rest-vs-smith itself, map pathing, and any combat play.
    /// </summary>
    public static DecisionKind? Classify(in ScreenContext ctx, RulesConfig cfg)
    {
        switch (ctx.Screen)
        {
            case ScreenKind.CardReward:
                if (!cfg.DelegateCardRewards) return null;
                if (ctx.Room == RoomKind.Event && !cfg.DelegateEventCardRewards) return null;
                return DecisionKind.CardReward;

            case ScreenKind.GeneratedCardChoice:
                // Outside combat these come from events, which stay with the owner.
                if (ctx.Room != RoomKind.Combat) return null;
                bool delegate_ = ctx.Source switch
                {
                    ChoiceSource.Potion => cfg.DelegatePotionChoices,
                    ChoiceSource.Relic => cfg.DelegateRelicChoices,
                    ChoiceSource.Card => cfg.DelegateCardEffectChoices,
                    _ => cfg.DelegateUnknownCombatChoices,
                };
                return delegate_ ? DecisionKind.GeneratedCardChoice : null;

            case ScreenKind.DeckGrid:
                if (ctx.Room == RoomKind.Merchant && ctx.ShopRemovalPending)
                    return DecisionKind.ShopRemovalPick;
                // The owner chose Smith over Rest; which card gets upgraded is the picker's call.
                if (ctx.Room == RoomKind.RestSite && cfg.DelegateSmith && IsSmith(ctx.LastRestOption))
                    return DecisionKind.SmithUpgrade;
                return null;

            case ScreenKind.ShopInventory:
                return cfg.DelegateShop && ctx.Room == RoomKind.Merchant ? DecisionKind.Shop : null;

            default:
                return null;
        }
    }

    public static bool IsSmith(string? restOptionId) =>
        restOptionId is not null && restOptionId.Contains("SMITH", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Attributes an in-combat generated choice to whatever most recently happened.
    /// Times are seconds on any monotonic clock; null means it never happened.
    /// </summary>
    public static ChoiceSource InferSource(double now, double? lastPotionUse, double? lastCardPlay, double? combatStart, double window = 3.0)
    {
        double? Age(double? t) => t is null ? null : now - t.Value;

        var potion = Age(lastPotionUse);
        var card = Age(lastCardPlay);
        bool potionRecent = potion is >= 0 && potion <= window;
        bool cardRecent = card is >= 0 && card <= window;

        if (potionRecent && cardRecent)
            return potion <= card ? ChoiceSource.Potion : ChoiceSource.Card;
        if (potionRecent) return ChoiceSource.Potion;
        if (cardRecent) return ChoiceSource.Card;

        // Toolbox-style relics fire as combat begins, before anything has been played.
        var sinceStart = Age(combatStart);
        if (sinceStart is >= 0 && sinceStart <= window) return ChoiceSource.Relic;

        return ChoiceSource.Unknown;
    }
}
