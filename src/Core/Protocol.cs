using System.Text;

namespace RingDraft.Core;

/// <summary>
/// A card as shown to the picker. Id is the model's type name; Title/Description are display text from the owner's client.
/// Blob is the game's own SerializableCard packet (base64), so the picker can rebuild the exact card,
/// upgrades and enchantments included, and show it on the game's real screens. Empty when not needed.
/// </summary>
public sealed record CardView(string Id, string Title, string Description, int Upgrades, string Blob = "");

public enum ShopItemType : byte { Card = 0, ColorlessCard = 1, Relic = 2, Potion = 3, CardRemoval = 4 }

/// <summary>
/// One merchant slot. Index is the slot's fixed position in the owner's shop (sold-out slots keep theirs and are sent with
/// Stocked = false), so the picker's copy of the shop can keep every item in its place. BaseCost is the price before relic
/// discounts, which the picker's game applies itself; Blob is the card itself for card slots, as in <see cref="CardView"/>.
/// </summary>
public sealed record ShopItemView(int Index, ShopItemType Type, string Id, string Title, string Description, int Cost, bool Affordable,
    bool Stocked = true, int BaseCost = 0, bool OnSale = false, string Blob = "");

public enum ShopActionType : byte { Buy = 0, Remove = 1, Done = 2 }

public enum CancelReason : byte { OwnerTookOver = 0, ScreenClosed = 1, Superseded = 2 }

/// <summary>Every message names who sent it and who it is for. Messages are broadcast; non-recipients ignore them.</summary>
public abstract record Envelope(uint RequestId, ulong From, ulong To);

/// <summary>Owner → picker: choose one of these for me.</summary>
public sealed record OfferMsg(
    uint RequestId, ulong From, ulong To,
    DecisionKind Kind,
    IReadOnlyList<CardView> Options,
    bool CanSkip,
    IReadOnlyList<CardView> Deck,
    int Gold,
    IReadOnlyList<string> Relics,
    int TimeoutSeconds) : Envelope(RequestId, From, To)
{
    /// <summary>
    /// For DecisionKind.Bundle: how many of Options belong to each pack, in order (e.g. [3,3,3]).
    /// The picker's answer is then a pack index. Empty for every other kind.
    /// </summary>
    public IReadOnlyList<int> Groups { get; init; } = Array.Empty<int>();

    /// <summary>Options split into packs according to Groups.</summary>
    public List<List<CardView>> Packs()
    {
        var packs = new List<List<CardView>>();
        int at = 0;
        foreach (int size in Groups)
        {
            if (size < 0 || at + size > Options.Count) break;
            packs.Add(Options.Skip(at).Take(size).ToList());
            at += size;
        }
        return packs;
    }

    /// <summary>How many answers there are: packs for a bundle, cards otherwise.</summary>
    public int ChoiceCount => Kind == DecisionKind.Bundle ? Groups.Count : Options.Count;
}

/// <summary>Picker → owner: option index, or -1 to skip.</summary>
public sealed record PickMsg(uint RequestId, ulong From, ulong To, int OptionIndex) : Envelope(RequestId, From, To);

/// <summary>Owner → picker: current shop contents. Re-sent after each purchase.</summary>
public sealed record ShopStateMsg(
    uint RequestId, ulong From, ulong To,
    int Gold,
    IReadOnlyList<ShopItemView> Items,
    IReadOnlyList<CardView> Deck,
    string Notice,
    int TimeoutSeconds) : Envelope(RequestId, From, To);

/// <summary>Picker → owner: buy an item, buy removal of a deck card, or finish shopping.</summary>
public sealed record ShopActionMsg(
    uint RequestId, ulong From, ulong To,
    ShopActionType Action,
    int ItemIndex,
    int DeckIndex) : Envelope(RequestId, From, To);

/// <summary>Owner → picker: stop, this decision is gone.</summary>
public sealed record CancelMsg(uint RequestId, ulong From, ulong To, CancelReason Reason) : Envelope(RequestId, From, To);

/// <summary>
/// Binary codec for envelopes. The game transport only carries strings we know how to write,
/// so <see cref="ToBase64"/>/<see cref="TryFromBase64"/> wrap it.
/// </summary>
public static class Codec
{
    public const byte Version = 3;
    private const int MaxList = 512;
    private const int MaxString = 4096;
    private const int MaxBlob = 2048;

    private enum Tag : byte { Offer = 1, Pick = 2, ShopState = 3, ShopAction = 4, Cancel = 5 }

    public static string ToBase64(Envelope e) => Convert.ToBase64String(Encode(e));

    public static bool TryFromBase64(string? s, out Envelope? envelope)
    {
        envelope = null;
        if (string.IsNullOrEmpty(s)) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(s); }
        catch (FormatException) { return false; }
        return TryDecode(bytes, out envelope);
    }

    public static byte[] Encode(Envelope e)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Version);
        switch (e)
        {
            case OfferMsg o:
                w.Write((byte)Tag.Offer); Head(w, o);
                w.Write((byte)o.Kind);
                Cards(w, o.Options);
                w.Write(o.CanSkip);
                Cards(w, o.Deck);
                w.Write(o.Gold);
                Strings(w, o.Relics);
                w.Write(o.TimeoutSeconds);
                Count(w, o.Groups.Count);
                foreach (int g in o.Groups) w.Write(g);
                break;
            case PickMsg p:
                w.Write((byte)Tag.Pick); Head(w, p);
                w.Write(p.OptionIndex);
                break;
            case ShopStateMsg s:
                w.Write((byte)Tag.ShopState); Head(w, s);
                w.Write(s.Gold);
                Count(w, s.Items.Count);
                foreach (var i in s.Items)
                {
                    w.Write(i.Index); w.Write((byte)i.Type); Str(w, i.Id); Str(w, i.Title); Str(w, i.Description);
                    w.Write(i.Cost); w.Write(i.Affordable);
                    w.Write(i.Stocked); w.Write(i.BaseCost); w.Write(i.OnSale);
                    w.Write(i.Blob is { Length: <= MaxBlob } blob ? blob : string.Empty);
                }
                Cards(w, s.Deck);
                Str(w, s.Notice);
                w.Write(s.TimeoutSeconds);
                break;
            case ShopActionMsg a:
                w.Write((byte)Tag.ShopAction); Head(w, a);
                w.Write((byte)a.Action); w.Write(a.ItemIndex); w.Write(a.DeckIndex);
                break;
            case CancelMsg c:
                w.Write((byte)Tag.Cancel); Head(w, c);
                w.Write((byte)c.Reason);
                break;
            default:
                throw new ArgumentException($"Unknown envelope type {e.GetType().Name}");
        }
        w.Flush();
        return ms.ToArray();
    }

    public static bool TryDecode(byte[] bytes, out Envelope? envelope)
    {
        envelope = null;
        try
        {
            using var ms = new MemoryStream(bytes);
            using var r = new BinaryReader(ms, Encoding.UTF8);
            if (r.ReadByte() != Version) return false;
            var tag = (Tag)r.ReadByte();
            uint id = r.ReadUInt32(); ulong from = r.ReadUInt64(); ulong to = r.ReadUInt64();
            switch (tag)
            {
                case Tag.Offer:
                {
                    var kind = (DecisionKind)r.ReadByte();
                    if (!Enum.IsDefined(kind)) return false;
                    var options = ReadCards(r);
                    bool canSkip = r.ReadBoolean();
                    var deck = ReadCards(r);
                    int gold = r.ReadInt32();
                    var relics = ReadStrings(r);
                    int timeout = r.ReadInt32();
                    int groupCount = ReadCount(r);
                    var groups = new List<int>(groupCount);
                    for (int g = 0; g < groupCount; g++)
                    {
                        int size = r.ReadInt32();
                        if (size < 0 || size > MaxList) return false;
                        groups.Add(size);
                    }
                    if (groups.Sum() > options.Count) return false;
                    envelope = new OfferMsg(id, from, to, kind, options, canSkip, deck, gold, relics, timeout) { Groups = groups };
                    break;
                }
                case Tag.Pick:
                    envelope = new PickMsg(id, from, to, r.ReadInt32());
                    break;
                case Tag.ShopState:
                {
                    int gold = r.ReadInt32();
                    int n = ReadCount(r);
                    var items = new List<ShopItemView>(n);
                    for (int k = 0; k < n; k++)
                    {
                        int index = r.ReadInt32();
                        var type = (ShopItemType)r.ReadByte();
                        if (!Enum.IsDefined(type)) return false;
                        var item = new ShopItemView(index, type, ReadStr(r), ReadStr(r), ReadStr(r), r.ReadInt32(), r.ReadBoolean(),
                            r.ReadBoolean(), r.ReadInt32(), r.ReadBoolean());
                        var itemBlob = r.ReadString();
                        if (itemBlob.Length > MaxBlob) throw new FormatException("Card blob too long");
                        items.Add(item with { Blob = itemBlob });
                    }
                    var deck = ReadCards(r);
                    string notice = ReadStr(r);
                    int timeout = r.ReadInt32();
                    envelope = new ShopStateMsg(id, from, to, gold, items, deck, notice, timeout);
                    break;
                }
                case Tag.ShopAction:
                {
                    var action = (ShopActionType)r.ReadByte();
                    if (!Enum.IsDefined(action)) return false;
                    envelope = new ShopActionMsg(id, from, to, action, r.ReadInt32(), r.ReadInt32());
                    break;
                }
                case Tag.Cancel:
                {
                    var reason = (CancelReason)r.ReadByte();
                    if (!Enum.IsDefined(reason)) return false;
                    envelope = new CancelMsg(id, from, to, reason);
                    break;
                }
                default:
                    return false;
            }
            // Trailing garbage means a version mismatch or corruption.
            if (ms.Position != ms.Length) { envelope = null; return false; }
            return true;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException)
        {
            envelope = null;
            return false;
        }
    }

    private static void Head(BinaryWriter w, Envelope e) { w.Write(e.RequestId); w.Write(e.From); w.Write(e.To); }

    private static void Count(BinaryWriter w, int n)
    {
        if (n > MaxList) throw new ArgumentException($"List too long ({n} > {MaxList})");
        w.Write(n);
    }

    private static int ReadCount(BinaryReader r)
    {
        int n = r.ReadInt32();
        if (n < 0 || n > MaxList) throw new FormatException("Bad list length");
        return n;
    }

    private static void Str(BinaryWriter w, string? s)
    {
        s ??= string.Empty;
        if (s.Length > MaxString) s = s[..MaxString];
        w.Write(s);
    }

    private static string ReadStr(BinaryReader r)
    {
        var s = r.ReadString();
        if (s.Length > MaxString) throw new FormatException("String too long");
        return s;
    }

    private static void Cards(BinaryWriter w, IReadOnlyList<CardView> cards)
    {
        Count(w, cards.Count);
        foreach (var c in cards)
        {
            Str(w, c.Id); Str(w, c.Title); Str(w, c.Description); w.Write(c.Upgrades);
            var blob = c.Blob ?? string.Empty;
            if (blob.Length > MaxBlob) blob = string.Empty; // too big to be a real card; picker falls back to the text list
            w.Write(blob);
        }
    }

    private static List<CardView> ReadCards(BinaryReader r)
    {
        int n = ReadCount(r);
        var list = new List<CardView>(n);
        for (int i = 0; i < n; i++)
        {
            var view = new CardView(ReadStr(r), ReadStr(r), ReadStr(r), r.ReadInt32());
            var blob = r.ReadString();
            if (blob.Length > MaxBlob) throw new FormatException("Card blob too long");
            list.Add(view with { Blob = blob });
        }
        return list;
    }

    private static void Strings(BinaryWriter w, IReadOnlyList<string> items)
    {
        Count(w, items.Count);
        foreach (var s in items) Str(w, s);
    }

    private static List<string> ReadStrings(BinaryReader r)
    {
        int n = ReadCount(r);
        var list = new List<string>(n);
        for (int i = 0; i < n; i++) list.Add(ReadStr(r));
        return list;
    }
}
