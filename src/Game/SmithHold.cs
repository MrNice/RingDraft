using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Smith goes last at the rest site. Clicking Smith normally opens the upgrade grid straight away, and its pick goes
/// to your picker, who is usually still choosing their own option; so the rest site turned into a queue. Now the click
/// waits (with a Cancel button) until every other player has chosen, or is waiting to Smith too, and then every
/// waiting Smith opens at once, so all the pickers choose upgrades at the same time.
///
/// Nothing reaches the game or the other players until the wait ends: the game only sends a rest site choice when
/// it's made, so a cancelled Smith never happened as far as the run is concerned.
/// </summary>
internal static class SmithHold
{
    /// <summary>Who is waiting to Smith, from everyone's RestHoldMsg.</summary>
    public static readonly SmithQueue Queue = new();

    /// <summary>Set each frame by the controller: Ring Draft is running in a co-op run and Smith picks are delegated.</summary>
    public static bool Enabled { get; set; }
    public static Action<uint, bool>? Broadcast { get; set; }
    public static Func<ulong, string> NameOf { get; set; } = id => id.ToString();

    [ThreadStatic] internal static bool Bypass;
    private static WaitPanel? _panel;
    private static bool _waiting;
    private static double _releasedAt = double.NegativeInfinity;

    /// <summary>
    /// This player's own Smith screen is about to open (they're waiting, or were just released). A neighbour's Smith
    /// screen that arrives now should open on top of it, not under it, so both pickers choose at the same time.
    /// </summary>
    public static bool OwnSmithComing(double now) => _waiting || now - _releasedAt < 3;

    internal static void MarkReleased() => _releasedAt = SourceTracker.Now();

    public static uint Floor()
    {
        try { return (uint)Math.Max(0, RunManager.Instance?.DebugOnlyGetState()?.TotalFloor ?? 0); }
        catch { return 0; }
    }

    /// <summary>Every other player as this client sees them: still choosing (an enabled option left that isn't Smith) or not.</summary>
    public static List<SmithQueue.Other> Others(RestSiteSynchronizer sync)
    {
        var list = new List<SmithQueue.Other>();
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null) return list;
        ulong me = GameQuery.Me()?.NetId ?? 0;
        foreach (var p in state.Players)
        {
            if (p.NetId == me) continue;
            bool choosing;
            try { choosing = sync.GetOptionsForPlayer(p.NetId).Any(o => o is not SmithRestSiteOption && o.IsEnabled); }
            catch { choosing = false; } // a player the rest site doesn't know about can't hold anyone up
            list.Add(new SmithQueue.Other(p.NetId, choosing));
        }
        return list;
    }

    /// <summary>Waits until Smith can go, then makes the choice for real. False if cancelled (the game re-enables the buttons).</summary>
    public static async Task<bool> WaitThenChoose(RestSiteSynchronizer sync, int index)
    {
        uint floor = Floor();
        var tree = (SceneTree)Engine.GetMainLoop();
        bool cancelled = false;
        _panel?.Close();
        _panel = WaitPanel.Show(tree.Root, () => cancelled = true);
        Broadcast?.Invoke(floor, true);
        _waiting = true;
        Log.Info("Smith waits until everyone else has chosen");

        try
        {
            while (true)
            {
                await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                if (cancelled || NRestSiteRoom.Instance is null || Floor() != floor)
                {
                    Broadcast?.Invoke(floor, false);
                    Log.Info("Smith wait cancelled");
                    return false;
                }
                var waitingOn = Queue.WaitingOn(Others(sync), floor);
                if (waitingOn.Count == 0) break;
                _panel?.SetWaitingOn(waitingOn.Select(NameOf).ToList());
            }
        }
        finally
        {
            _panel?.Close();
            _panel = null;
            _waiting = false;
        }
        _releasedAt = SourceTracker.Now();

        // Still marked as waiting while the upgrade is picked, so the other Smiths don't queue up behind this one.
        Log.Info("Everyone has chosen: Smithing now");
        Task<bool> choose;
        Bypass = true;
        try { choose = sync.ChooseLocalOption(index); }
        finally { Bypass = false; }
        return await choose;
    }

    /// <summary>The waiting message over the rest site, with a Cancel button.</summary>
    private sealed class WaitPanel
    {
        private readonly CanvasLayer _layer;
        private readonly Label _detail;
        private string _last = "";

        private WaitPanel(CanvasLayer layer, Label detail)
        {
            _layer = layer;
            _detail = detail;
        }

        public static WaitPanel Show(Node root, Action onCancel)
        {
            var layer = new CanvasLayer { Name = "RingDraftSmithWait", Layer = 60 };
            root.AddChild(layer);

            var anchor = new Control { MouseFilter = Control.MouseFilterEnum.Ignore };
            layer.AddChild(anchor);
            anchor.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

            var panel = new PanelContainer { MouseFilter = Control.MouseFilterEnum.Stop };
            panel.AddThemeStyleboxOverride("panel", Style.Box(Style.Panel, 12, 18));
            anchor.AddChild(panel);
            panel.AnchorLeft = panel.AnchorRight = 0.5f;
            panel.AnchorTop = panel.AnchorBottom = 0.62f;
            panel.GrowHorizontal = Control.GrowDirection.Both;
            panel.GrowVertical = Control.GrowDirection.Both;
            panel.CustomMinimumSize = new Vector2(460, 0);

            var box = new VBoxContainer();
            box.AddThemeConstantOverride("separation", 10);
            panel.AddChild(box);
            var title = Style.Label("Smith goes last", 24, Style.Accent);
            title.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(title);
            var detail = Style.Label("Waiting for everyone else to choose…", 18);
            detail.HorizontalAlignment = HorizontalAlignment.Center;
            box.AddChild(detail);
            var cancel = Style.Button("Cancel", onCancel);
            cancel.Alignment = HorizontalAlignment.Center;
            box.AddChild(cancel);

            return new WaitPanel(layer, detail);
        }

        public void SetWaitingOn(List<string> names)
        {
            string text = names.Count switch
            {
                0 => "Waiting for everyone else to choose…",
                1 => $"Waiting for {names[0]} to choose…",
                _ => $"Waiting for {string.Join(", ", names.Take(names.Count - 1))} and {names[^1]} to choose…",
            };
            if (text == _last || !GodotObject.IsInstanceValid(_detail)) return;
            _last = text;
            _detail.Text = text;
        }

        public void Close()
        {
            if (GodotObject.IsInstanceValid(_layer)) _layer.QueueFree();
        }
    }
}

/// <summary>Holds a Smith click until the other players have chosen (see <see cref="SmithHold"/>).</summary>
[HarmonyPatch(typeof(RestSiteSynchronizer), nameof(RestSiteSynchronizer.ChooseLocalOption))]
internal static class SmithHoldPatch
{
    private static bool Prefix(RestSiteSynchronizer __instance, int __0, ref Task<bool> __result)
    {
        if (SmithHold.Bypass || !SmithHold.Enabled) return true;
        try
        {
            var options = __instance.GetLocalOptions();
            if (__0 < 0 || __0 >= options.Count || options[__0] is not SmithRestSiteOption) return true;
            if (SmithHold.Queue.CanSmithNow(SmithHold.Others(__instance), SmithHold.Floor()))
            {
                SmithHold.MarkReleased();
                // Nobody to wait for, but still say so: other Smiths waiting on this player can go too.
                SmithHold.Broadcast?.Invoke(SmithHold.Floor(), true);
                return true;
            }
            __result = SmithHold.WaitThenChoose(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Smith hold failed; smithing straight away", ex);
            return true;
        }
    }
}
