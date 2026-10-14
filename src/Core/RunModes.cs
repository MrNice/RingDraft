namespace RingDraft.Core;

/// <summary>How a co-op run is played, chosen in the lobby.</summary>
public enum RunMode : byte
{
    /// <summary>The game as it comes: Ring Draft stays out of the way.</summary>
    Normal = 0,
    /// <summary>Your neighbour drafts your deck.</summary>
    RingDraft = 1,
    /// <summary>Ring Draft where the pickers try to sink each other's decks; the last player alive wins.</summary>
    RingDraftPvP = 2,
}
