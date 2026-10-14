namespace RingDraft.Core;

/// <summary>
/// Smithing goes last at the rest site. Choosing Smith hands the upgrade pick to your picker, who is often still
/// choosing their own rest site option; so a player who clicks Smith waits until every other player has either
/// finished choosing or is waiting to Smith too, and then all the waiting Smiths open together.
/// </summary>
public sealed class SmithQueue
{
    /// <summary>One other player as this client sees them at the rest site.</summary>
    /// <param name="CanStillChoose">They have an enabled option left that isn't Smith. False once they've chosen,
    /// unless a relic like Miniature Tent lets them keep choosing (then it's false once only Smith is left).</param>
    public readonly record struct Other(ulong Id, bool CanStillChoose);

    private readonly Dictionary<ulong, uint> _waiting = new();

    /// <summary>A player's RestHoldMsg: waiting (or not) to Smith at the rest site on <paramref name="floor"/>.</summary>
    public void Set(ulong player, uint floor, bool waiting)
    {
        if (waiting) _waiting[player] = floor;
        else _waiting.Remove(player);
    }

    public bool IsWaiting(ulong player, uint floor) => _waiting.TryGetValue(player, out var f) && f == floor;

    public void Clear() => _waiting.Clear();

    /// <summary>The players this player's Smith is waiting on: still choosing, and not waiting to Smith themselves.</summary>
    public List<ulong> WaitingOn(IEnumerable<Other> others, uint floor) =>
        others.Where(o => o.CanStillChoose && !IsWaiting(o.Id, floor)).Select(o => o.Id).ToList();

    public bool CanSmithNow(IEnumerable<Other> others, uint floor) => WaitingOn(others, floor).Count == 0;
}
