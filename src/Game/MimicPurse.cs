using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Potions;
using MegaCrit.sts2.Core.Nodes.TopBar;

namespace RingDraft;

/// <summary>
/// The owner's gold and potion belt, inline in the picker's top bar right after the picker's own:
/// coin, my gold, my potions, coin, their gold, their potions. Both are copies of the top bar's own
/// pieces (same font, same count-down when gold is spent, same belt), bound to the owner instead of the
/// local player. The owner's coin is recoloured to their character's colour so the two purses read apart.
/// Nothing in it can be clicked: the potions belong to someone else.
/// </summary>
internal sealed class MimicPurse
{
    private const string CopyMeta = "ringdraft_purse_copy";
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const int Copy = (int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation);

    /// <summary>Clips the purse while it slides open and shut in the top bar's row.</summary>
    private readonly Control _wrap;
    private readonly HBoxContainer _row;
    private readonly Player _owner;
    private readonly NPotionContainer? _potions;
    private Tween? _tween;
    private bool _closing;

    private MimicPurse(Control wrap, HBoxContainer row, Player owner, NPotionContainer? potions)
    {
        _wrap = wrap;
        _row = row;
        _owner = owner;
        _potions = potions;
    }

    /// <summary>
    /// Adds the owner's purse to the top bar. It closes with the shop, and goes at once if
    /// <paramref name="shopNode"/> disappears without a proper close. Null if the top bar isn't laid out as expected.
    /// </summary>
    public static MimicPurse? Create(Node shopNode, Player owner)
    {
        var bar = NRun.Instance?.GlobalUi?.TopBar;
        var belt = bar?.PotionContainer;
        var gold = bar?.Gold;
        if (bar is null || belt is null || gold is null || !Nodes.Alive(bar)) return null;
        // The top bar's row: portrait, HP, gold, potion belt (in a margin container), room icons, ...
        var beltSlot = belt.GetParent() as Control;
        if (beltSlot?.GetParent() is not HBoxContainer row) return null;

        var wrap = new Control { Name = "RingDraftPurse", ClipContents = true, MouseFilter = Control.MouseFilterEnum.Ignore };
        var inner = new HBoxContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        inner.AddThemeConstantOverride("separation", row.GetThemeConstant("separation"));
        wrap.AddChild(inner);

        var goldCopy = CopyGold(bar, gold, owner);
        if (goldCopy is not null) inner.AddChild(goldCopy);
        var potions = CopyPotions(beltSlot, belt, inner, owner);
        if (inner.GetChildCount() == 0)
        {
            wrap.QueueFree();
            return null;
        }

        row.AddChild(wrap);
        row.MoveChild(wrap, beltSlot.GetIndex() + 1);
        // Bound to the owner once in the tree, where the copy has found its labels.
        if (goldCopy is not null)
        {
            try { goldCopy.Initialize(owner); }
            catch (Exception ex)
            {
                Log.Warn($"The gold counter copy didn't start: {ex.Message}");
                goldCopy.QueueFree();
            }
        }
        if (potions is not null) BindPotions(potions, owner);
        Unclickable(wrap);

        var purse = new MimicPurse(wrap, inner, owner, potions);
        shopNode.TreeExiting += () => { if (GodotObject.IsInstanceValid(wrap)) wrap.QueueFree(); };
        Callable.From(purse.AnimateIn).CallDeferred();
        return purse;
    }

    /// <summary>Centre of the belt slot the next potion goes into, if the belt is showing and has room.</summary>
    public Vector2? NextPotionSlot()
    {
        if (_potions is null || !Nodes.Alive(_potions)) return null;
        var holders = Holders(_potions);
        var slots = _owner.PotionSlots;
        for (int i = 0; i < holders.Count && i < slots.Count; i++)
        {
            if (slots[i] is not null || !Nodes.Alive(holders[i])) continue;
            var xf = holders[i].GetGlobalTransformWithCanvas();
            return xf.Origin + holders[i].Size * xf.Scale * 0.5f;
        }
        return null;
    }

    /// <summary>The belt's own "no room" shake.</summary>
    public void PotionsFull()
    {
        if (_potions is null || !Nodes.Alive(_potions)) return;
        try { AccessTools.Method(typeof(NPotionContainer), "PlayAddFailedAnim")?.Invoke(_potions, null); }
        catch (Exception ex) { Log.Warn($"Potion belt shake failed: {ex.Message}"); }
    }

    /// <summary>Slides open in the top bar (pushing the room icons along), as the rug drops in.</summary>
    private void AnimateIn()
    {
        if (!Nodes.Alive(_wrap)) return;
        var size = _row.GetCombinedMinimumSize();
        _row.Size = size;
        _wrap.CustomMinimumSize = new Vector2(0, size.Y);
        _wrap.Modulate = new Color(1, 1, 1, 0);
        _row.MinimumSizeChanged += () => { if (Nodes.Alive(_row)) _row.Size = _row.GetCombinedMinimumSize(); };
        _tween?.Kill();
        _tween = _wrap.CreateTween().SetParallel();
        _tween.TweenProperty(_wrap, "custom_minimum_size:x", size.X, 0.45).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.Out);
        _tween.TweenProperty(_wrap, "modulate:a", 1f, 0.3).SetDelay(0.1);
    }

    /// <summary>Slides shut and fades as the rug lifts away, then is gone.</summary>
    public void AnimateOut()
    {
        if (_closing || !Nodes.Alive(_wrap)) return;
        _closing = true;
        var wrap = _wrap;
        _tween?.Kill();
        _tween = wrap.CreateTween().SetParallel();
        _tween.TweenProperty(wrap, "modulate:a", 0f, 0.3);
        _tween.TweenProperty(wrap, "custom_minimum_size:x", 0f, 0.45).SetDelay(0.15).SetTrans(Tween.TransitionType.Cubic).SetEase(Tween.EaseType.In);
        _tween.Chain().TweenCallback(Callable.From(() => { if (GodotObject.IsInstanceValid(wrap)) wrap.QueueFree(); }));
    }

    // ------------------------------------------------------------------ the copies

    /// <summary>
    /// The top bar's gold counter (coin and number), script and all, so it counts down the owner's gold with the game's
    /// own animation. Its labels are found by unique name, which only works inside the scene that owns them, so the
    /// copy is made the owner of its own label and of a copy of the "+/-" popup that the real one borrows from the bar.
    /// </summary>
    private static NTopBarGold? CopyGold(Node bar, NTopBarGold gold, Player owner)
    {
        try
        {
            var copy = (NTopBarGold)gold.Duplicate(Copy);
            copy.Name = "OwnerGold";
            copy.SetMeta(CopyMeta, true);
            var label = copy.GetNodeOrNull<Control>("GoldLabel") ?? throw new InvalidOperationException("no GoldLabel");
            Own(copy, label, "GoldLabel");

            if (bar.GetNodeOrNull<Control>("GoldPopup") is { } popupSource && gold.GetNodeOrNull<Control>("GoldLabel") is { } sourceLabel)
            {
                var popup = (Control)popupSource.Duplicate(Copy);
                popup.Name = "GoldPopup";
                label.AddChild(popup);
                // Same place relative to the number as the real popup.
                popup.Position = popupSource.GetGlobalRect().Position - sourceLabel.GetGlobalRect().Position;
                Own(copy, popup, "GoldPopup");
            }

            if (copy.GetNodeOrNull<CanvasItem>("GoldIcon") is { } coin)
                coin.Material = CoinMaterial(CharacterColor(owner));
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't copy the gold counter: {ex.Message}");
            return null;
        }
    }

    private static void Own(Node owner, Node node, string uniqueName)
    {
        node.UniqueNameInOwner = false;
        node.Owner = owner;
        node.Name = uniqueName;
        node.UniqueNameInOwner = true;
    }

    /// <summary>The potion belt with its frame, emptied of the local player's slots; the owner's are made in <see cref="BindPotions"/>.</summary>
    private static NPotionContainer? CopyPotions(Control beltSlot, NPotionContainer belt, Container into, Player owner)
    {
        try
        {
            var slotCopy = (Control)beltSlot.Duplicate(Copy);
            slotCopy.Name = "OwnerPotions";
            slotCopy.SetMeta(CopyMeta, true);
            var copy = slotCopy.GetNodeOrNull<NPotionContainer>(beltSlot.GetPathTo(belt)) ?? throw new InvalidOperationException("no potion container");
            if (copy.GetNodeOrNull("MarginContainer/PotionHolders") is { } holders)
                foreach (var child in holders.GetChildren())
                {
                    holders.RemoveChild(child);
                    child.QueueFree();
                }
            if (copy.GetNodeOrNull<CanvasItem>("PotionShortcutButton") is { } shortcut) shortcut.Visible = false;
            into.AddChild(slotCopy);
            return copy;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't copy the potion belt: {ex.Message}");
            return null;
        }
    }

    /// <summary>Set up as the game sets up the real belt, but for the owner: their slot count, potions and potion events.</summary>
    private static void BindPotions(NPotionContainer copy, Player owner)
    {
        try
        {
            typeof(NPotionContainer).GetField("_player", Any)!.SetValue(copy, owner);
            AccessTools.Method(typeof(NPotionContainer), "ConnectPlayerEvents")!.Invoke(copy, null);
            AccessTools.Method(typeof(NPotionContainer), "GrowPotionHolders")!.Invoke(copy, new object[] { owner.MaxPotionCount });
            var add = AccessTools.Method(typeof(NPotionContainer), "Add", new[] { typeof(PotionModel), typeof(bool) })!;
            foreach (var potion in owner.Potions) add.Invoke(copy, new object[] { potion, true });
        }
        catch (Exception ex) { Log.Warn($"Couldn't fill the owner's potion belt: {ex.Message}"); }
    }

    private static Color CharacterColor(Player owner)
    {
        try
        {
            if (owner.Character is { } c && c.NameColor.A > 0.1f) return new Color(c.NameColor, 1f);
        }
        catch { /* fall through */ }
        return new Color(0.75f, 0.75f, 0.8f);
    }

    /// <summary>Repaints the gold coin in a colour, keeping its shading (and its dark outline).</summary>
    private static ShaderMaterial CoinMaterial(Color tint)
    {
        var shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                uniform vec4 tint : source_color;
                void fragment() {
                    vec4 c = texture(TEXTURE, UV) * COLOR;
                    float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
                    vec3 shaded = tint.rgb * smoothstep(0.0, 0.85, l) * 1.35;
                    COLOR = vec4(mix(shaded, vec3(l), 0.12), c.a);
                }
                """,
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("tint", tint);
        return material;
    }

    private static List<NPotionHolder> Holders(NPotionContainer container) =>
        typeof(NPotionContainer).GetField("_holders", Any)?.GetValue(container) as List<NPotionHolder> ?? new();

    /// <summary>Puts a potion the owner just got into the belt, with the game's bounce, once its flying icon has landed.</summary>
    internal static async void AddWhenLanded(NPotionContainer copy, PotionModel potion)
    {
        try
        {
            if (potion.Owner is { } p && MimicFly.TryLanding(p.NetId, out var landing)) await landing;
            if (!Nodes.Alive(copy)) return;
            AccessTools.Method(typeof(NPotionContainer), "Add", new[] { typeof(PotionModel), typeof(bool) })?.Invoke(copy, new object[] { potion, true });
            Unclickable(copy);
            if (potion.Owner is { } owner && owner.GetPotionSlotIndex(potion) is var i and >= 0
                && Holders(copy) is { } holders && i < holders.Count && holders[i].Potion is { } node)
                node.DoBounce();
        }
        catch (Exception ex) { Log.Warn($"Showing the new potion in the belt failed: {ex.Message}"); }
    }

    /// <summary>The copies show, they don't do: no clicks, no focus, no potion menu.</summary>
    private static void Unclickable(Node node)
    {
        if (node is Control c)
        {
            c.MouseFilter = Control.MouseFilterEnum.Ignore;
            c.FocusMode = Control.FocusModeEnum.None;
        }
        foreach (var child in node.GetChildren()) Unclickable(child);
    }

    /// <summary>True for nodes inside one of the copies.</summary>
    internal static bool IsCopy(Node? node)
    {
        for (var n = node; n is not null; n = n.GetParent())
            if (n.HasMeta(CopyMeta)) return true;
        return false;
    }
}

/// <summary>A potion arriving in the owner's belt copy waits for its flying icon, and never triggers the potion tutorial.</summary>
[HarmonyPatch(typeof(NPotionContainer), "OnPotionProcured")]
internal static class PurseProcuredPatch
{
    private static bool Prefix(NPotionContainer __instance, PotionModel __0)
    {
        if (!MimicPurse.IsCopy(__instance)) return true;
        MimicPurse.AddWhenLanded(__instance, __0);
        return false;
    }
}

/// <summary>The owner's potions can't be used or thrown away from the picker's screen.</summary>
[HarmonyPatch(typeof(NPotionHolder), "OpenPotionPopup")]
internal static class PursePotionPopupPatch
{
    private static bool Prefix(NPotionHolder __instance) => !MimicPurse.IsCopy(__instance);
}

/// <summary>The belt copy's focus links stay out of the real top bar's controller navigation.</summary>
[HarmonyPatch(typeof(NPotionContainer), "UpdateNavigation")]
internal static class PurseNavigationPatch
{
    private static bool Prefix(NPotionContainer __instance) => !MimicPurse.IsCopy(__instance);
}
