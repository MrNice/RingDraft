namespace RingDraft.Core;

/// <summary>
/// Ring Draft PvP: the pickers try to sink each other's decks and the last player alive wins. These are the rules
/// that don't need the game, so they can be tested.
/// </summary>
public static class PvpRules
{
    /// <summary>One skip charge per this many floors.</summary>
    public const int FloorsPerSkip = 3;

    /// <summary>Which skip window a floor is in: floors 1–3 are window 0, 4–6 window 1, and so on.</summary>
    public static int SkipWindow(int floor) => Math.Max(0, floor - 1) / FloorsPerSkip;

    /// <summary>
    /// Who won, once it's decided: the one player still standing, or 0 when nobody is (everyone fell together, so
    /// everyone loses). Null while two or more are still alive, or with fewer than two players (nothing to win).
    /// </summary>
    public static ulong? Victor(IReadOnlyCollection<(ulong Id, bool Alive)> players)
    {
        if (players.Count < 2) return null;
        var alive = players.Where(p => p.Alive).Select(p => p.Id).ToList();
        return alive.Count switch
        {
            0 => 0UL,
            1 => alive[0],
            _ => null,
        };
    }

    /// <summary>
    /// After a tie: was this player part of it (fell on the final turn), or did they fall on an earlier turn and miss
    /// it? <paramref name="fellOnTurn"/> is null when their fall wasn't seen before the end (it was the final blow).
    /// </summary>
    public static bool InTie(int? fellOnTurn, int finalTurn) => fellOnTurn is null || fellOnTurn >= finalTurn;
}

/// <summary>A player's card reward skips in PvP: one per <see cref="PvpRules.FloorsPerSkip"/> floors.</summary>
public sealed class SkipCharges
{
    private int? _usedInWindow;

    public bool CanSkip(int floor) => _usedInWindow != PvpRules.SkipWindow(floor);

    public void Used(int floor) => _usedInWindow = PvpRules.SkipWindow(floor);

    public void Reset() => _usedInWindow = null;
}
