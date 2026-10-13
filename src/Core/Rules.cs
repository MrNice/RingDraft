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
    /// <summary>"Choose a pack": pick one of several bundles of cards (Scroll Boxes).</summary>
    Bundle,
    Other,
}

/// <summary>Which deck grid is open: what choosing cards on it will do.</summary>
public enum DeckGridKind { Other, Upgrade, Remove, Transform }

/// <summary>Ancient is Neow and the other Ancients: an event room whose event is an Ancient.</summary>
public enum RoomKind { Other, Combat, Merchant, RestSite, Event, Treasure, Ancient }

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
    /// <summary>Choose one pack of cards out of several.</summary>
    Bundle = 6,
    /// <summary>Cards to remove from the deck, from a gift like Neow's Precise Scissors or Precarious Shears.</summary>
    DeckRemove = 7,
    /// <summary>Cards to transform, from a gift like Neow's New Leaf.</summary>
    DeckTransform = 8,
    /// <summary>Cards to upgrade, from a gift like Pomander (Smith has its own kind).</summary>
    DeckUpgrade = 9,
}

public sealed class RulesConfig
{
    public bool DelegateCardRewards { get; set; } = true;
    /// <summary>Card reward screens that come out of an event. Off by default: events stay with their owner.</summary>
    public bool DelegateEventCardRewards { get; set; } = false;
    public bool DelegateShop { get; set; } = true;
    public bool DelegateSmith { get; set; } = true;
    public bool DelegatePotionChoices { get; set; } = true;
    /// <summary>Card choices from relics: Toolbox at combat start, and Hefty Tablet / Massive Scroll / Scroll Boxes picked up at a shop or chest.</summary>
    public bool DelegateRelicChoices { get; set; } = true;
    /// <summary>
    /// The cards an Ancient's gift hands out (Neow and the act Ancients): card rewards from Lost Coffer / Orrery / Glass Eye,
    /// choose-a-card from Hefty Tablet / Massive Scroll, packs from Scroll Boxes. The owner still chooses which gift to take.
    /// </summary>
    public bool DelegateAncientRewards { get; set; } = true;
    /// <summary>Choices created by playing a card (Discovery-style). Off by default: playing cards is the owner's turn.</summary>
    public bool DelegateCardEffectChoices { get; set; } = false;
    /// <summary>In-combat generated choices whose source could not be determined.</summary>
    public bool DelegateUnknownCombatChoices { get; set; } = true;
    /// <summary>
    /// When a neighbour opens a card reward, open this player's own card reward too, so everyone picks at once
    /// instead of taking turns on the rewards screen.
    /// </summary>
    public bool OpenAllCardRewards { get; set; } = true;
    /// <summary>
    /// The shop works the same way: when a neighbour opens their shop, this player's opens too, and each player's shop
    /// closes when their picker is done with it.
    /// </summary>
    public bool OpenAllShops { get; set; } = true;
    /// <summary>
    /// In combat (Toolbox, a teammate's Attack/Skill/Power potion), open the picker's card screen straight away.
    /// Off: the picker gets the list with a "Show the cards" button, so nothing covers their hand mid-turn.
    /// </summary>
    public bool AutoOpenInCombat { get; set; } = true;
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
    bool ShopRemovalPending,
    DeckGridKind Grid = DeckGridKind.Other);

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
                if (ctx.Room == RoomKind.Ancient && !cfg.DelegateAncientRewards) return null;
                return DecisionKind.CardReward;

            case ScreenKind.Bundle:
                return OutOfCombatRelicChoice(ctx.Room, cfg) ? DecisionKind.Bundle : null;

            case ScreenKind.GeneratedCardChoice:
                // Out of combat these come from a relic's pickup effect (or an event, which stays with the owner).
                if (ctx.Room != RoomKind.Combat)
                    return OutOfCombatRelicChoice(ctx.Room, cfg) ? DecisionKind.GeneratedCardChoice : null;
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
                // Which cards a gift removes, transforms or upgrades. The owner already chose the gift.
                if (!GiftDeckChoice(ctx.Room, cfg)) return null;
                return ctx.Grid switch
                {
                    DeckGridKind.Remove => DecisionKind.DeckRemove,
                    DeckGridKind.Transform => DecisionKind.DeckTransform,
                    DeckGridKind.Upgrade => DecisionKind.DeckUpgrade,
                    _ => null,
                };

            case ScreenKind.ShopInventory:
                return cfg.DelegateShop && ctx.Room == RoomKind.Merchant ? DecisionKind.Shop : null;

            default:
                return null;
        }
    }

    /// <summary>Regular events stay with the owner; Ancients follow their own switch; anywhere else it's a relic someone picked up.</summary>
    private static bool OutOfCombatRelicChoice(RoomKind room, RulesConfig cfg) => room switch
    {
        RoomKind.Event => false,
        RoomKind.Ancient => cfg.DelegateAncientRewards,
        RoomKind.Combat => false,
        _ => cfg.DelegateRelicChoices,
    };

    /// <summary>
    /// A deck grid opened by a relic's pickup effect: at an Ancient (Neow's Precise Scissors, New Leaf...), or from a
    /// chest or on the map. Not in events or rest sites (the owner's own choices there), and not at the merchant,
    /// where the only removal is the one the picker already chose.
    /// </summary>
    private static bool GiftDeckChoice(RoomKind room, RulesConfig cfg) => room switch
    {
        RoomKind.Ancient => cfg.DelegateAncientRewards,
        RoomKind.Treasure or RoomKind.Other => cfg.DelegateRelicChoices,
        _ => false,
    };

    /// <summary>Remove, transform or upgrade cards from the owner's deck: answered with one or more card indices.</summary>
    public static bool IsDeckChoice(DecisionKind kind) =>
        kind is DecisionKind.DeckRemove or DecisionKind.DeckTransform or DecisionKind.DeckUpgrade;

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
