namespace RingDraft.Core;

/// <summary>
/// Seat order for the ring. Seat i picks for seat i+1 (the last seat picks for the first).
/// With two players this is a straight swap.
/// </summary>
public sealed class RingOrder
{
    private readonly ulong[] _seats;

    public RingOrder(IEnumerable<ulong> seatOrder, bool reverse = false)
    {
        var seen = new HashSet<ulong>();
        var list = new List<ulong>();
        foreach (var id in seatOrder)
            if (seen.Add(id)) list.Add(id);
        if (reverse) list.Reverse();
        _seats = list.ToArray();
    }

    public int Count => _seats.Length;
    public IReadOnlyList<ulong> Seats => _seats;
    public bool Contains(ulong id) => Array.IndexOf(_seats, id) >= 0;

    /// <summary>
    /// The player who makes choices for <paramref name="target"/>: the seat before it.
    /// Players for whom <paramref name="isAvailable"/> returns false are skipped, so the
    /// next seat back takes over. Returns null when nobody else can pick, meaning the
    /// owner keeps the choice (solo, or everyone else gone).
    /// </summary>
    public ulong? PickerFor(ulong target, Func<ulong, bool>? isAvailable = null)
    {
        int t = Array.IndexOf(_seats, target);
        if (t < 0 || _seats.Length < 2) return null;

        for (int step = 1; step < _seats.Length; step++)
        {
            ulong candidate = _seats[(t - step + _seats.Length) % _seats.Length];
            if (isAvailable is null || isAvailable(candidate))
                return candidate;
        }
        return null;
    }

    /// <summary>The player <paramref name="picker"/> normally picks for (the next seat).</summary>
    public ulong? TargetOf(ulong picker)
    {
        int p = Array.IndexOf(_seats, picker);
        if (p < 0 || _seats.Length < 2) return null;
        return _seats[(p + 1) % _seats.Length];
    }
}
