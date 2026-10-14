using System.Collections.Concurrent;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Runs;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// The host picks the run's mode in the co-op lobby (character select): their ready button becomes three, stacked
/// on the right. The game's own, labelled Normal, then Ring Draft and Ring Draft PvP below it, smaller, with PvP's
/// button painted red. Readying with one of them sets the mode. Everyone else keeps the single ready button, labelled
/// with the mode once the host has picked it.
/// </summary>
internal static class LobbyModes
{
    private static NCharacterSelectScreen? _screen;
    private static StartRunLobby? _lobby;
    private static INetGameService? _net;
    private static NConfirmButton? _normal;
    private static Label? _normalLabel;
    private static readonly List<NConfirmButton> _copies = new();
    // The smaller copies live in this, scaled down, so the game's own hover pulse (1 → 1.05 → 1) works on them as is.
    private static Control? _small;
    // Each button's mode name, beside the button rather than on it: it doesn't pulse, and fades as the button leaves.
    private static readonly List<Tag> _tags = new();

    private sealed class Tag
    {
        public required NConfirmButton Button { get; init; }
        public required Label Label { get; init; }
        public required bool Small { get; init; }
        public bool? Shown { get; set; }
        public Tween? Fade { get; set; }
    }
    private static readonly ConcurrentQueue<(ModeMsg Msg, ulong Sender)> Incoming = new();
    private static bool _checkBegin;
    private static int _playerCount;

    /// <summary>The host's pick, on every client (null until they ready up).</summary>
    public static RunMode? HostMode { get; private set; }

    private static bool IsHost => _net?.Type == NetGameType.Host;

    private static readonly string[] Labels = { "Normal", "Ring Draft", "Ring Draft PvP" };
    private const float SmallScale = 0.6f;
    private static readonly Color PvpRed = new(0.95f, 0.2f, 0.16f);

    /// <summary>Called once the character select screen is set up as a co-op lobby.</summary>
    public static void Attach(NCharacterSelectScreen screen)
    {
        Detach();
        var lobby = screen.Lobby;
        if (lobby?.NetService is not { } net || !net.Type.IsMultiplayer()) return;
        _screen = screen;
        _lobby = lobby;
        _net = net;
        HostMode = null;
        _playerCount = 0;
        try
        {
            net.RegisterMessageHandler<RingDraftMessage>(OnMessage);
            _normal = Traverse.Create(screen).Field("_embarkButton").GetValue<NConfirmButton>()
                ?? throw new InvalidOperationException("no embark button");
            if (IsHost) BuildHostButtons();
            else _normalLabel = AddLabel(_normal, "", small: false);
            UpdateTags();
            Log.Info(IsHost ? "Lobby: Normal / Ring Draft / Ring Draft PvP ready buttons added" : "Lobby: the host picks the mode");
        }
        catch (Exception ex) { Log.Error("Adding the lobby mode buttons failed", ex); }
    }

    private static void Detach()
    {
        if (_net is not null)
        {
            try { _net.UnregisterMessageHandler<RingDraftMessage>(OnMessage); } catch { /* already gone */ }
        }
        foreach (var b in _copies) if (GodotObject.IsInstanceValid(b)) b.QueueFree();
        _copies.Clear();
        foreach (var t in _tags) if (GodotObject.IsInstanceValid(t.Label)) t.Label.QueueFree();
        _tags.Clear();
        if (_small is not null && GodotObject.IsInstanceValid(_small)) _small.QueueFree();
        _small = null;
        _normalLabel = null;
        _normal = null;
        _screen = null;
        _lobby = null;
        _net = null;
        while (Incoming.TryDequeue(out _)) { }
    }

    /// <summary>Every frame, from the controller.</summary>
    public static void Step()
    {
        if (_screen is null) return;
        if (!Nodes.Alive(_screen) || _lobby is null)
        {
            Detach();
            return;
        }

        while (Incoming.TryDequeue(out var item))
        {
            if (item.Msg.RequestId != ModeMsg.LobbyVote || item.Msg.From != item.Sender || IsHost) continue;
            HostMode = item.Msg.Mode;
            if (_normalLabel is not null && GodotObject.IsInstanceValid(_normalLabel)) _normalLabel.Text = Labels[(int)HostMode.Value];
        }

        // Someone joined: tell them what the host picked.
        int players = PlayerCount(_lobby);
        if (players != _playerCount)
        {
            _playerCount = players;
            if (IsHost && HostMode is { } mode) Send(mode);
        }

        // The copies follow the game's own button: they slide away once the host is ready, and back on unready.
        if (_normal is not null && GodotObject.IsInstanceValid(_normal))
            foreach (var b in _copies)
                if (GodotObject.IsInstanceValid(b) && b.IsEnabled != _normal.IsEnabled) b.SetEnabled(_normal.IsEnabled);
        UpdateTags();

        if (_checkBegin && IsHost)
        {
            _checkBegin = false;
            try { AccessTools.Method(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")?.Invoke(_lobby, null); }
            catch (Exception ex) { Log.Error("Starting the run after picking a mode failed", ex); }
        }
    }

    /// <summary>
    /// How many players are in the lobby. Read by name: the list's element type differs between game v0.107
    /// (LobbyPlayer) and the v0.111 beta (StartRunLobbyPlayer), and one build runs on both.
    /// </summary>
    private static int PlayerCount(StartRunLobby lobby) =>
        lobby.GetType().GetProperty("Players")?.GetValue(lobby) is System.Collections.ICollection list ? list.Count : 0;

    public static bool IsOurLobby(StartRunLobby lobby) => ReferenceEquals(lobby, _lobby);
    public static bool HostHasPicked => HostMode is not null;
    public static bool LocalIsHost => IsHost;

    // ------------------------------------------------------------------ host buttons

    private static void BuildHostButtons()
    {
        var normal = _normal!;
        normal.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => Pick(RunMode.Normal)));
        AddLabel(normal, Labels[0], small: false);

        // A holder at 60%, pinned to the bottom-right corner of the screen (see PlaceSmall), for the copies.
        var parent = normal.GetParent();
        _small = new Control { Name = "RingDraftSmallButtons", MouseFilter = Control.MouseFilterEnum.Ignore, Scale = Vector2.One * SmallScale };
        parent.AddChild(_small);
        parent.MoveChild(_small, normal.GetIndex() + 1);
        PlaceSmall();

        float height = normal.Size.Y > 0 ? normal.Size.Y : normal.OffsetBottom - normal.OffsetTop;
        float drop = height * 0.92f; // the first copy sits right under the game's button
        foreach (var mode in new[] { RunMode.RingDraft, RunMode.RingDraftPvP })
        {
            var copy = (NConfirmButton)normal.Duplicate((int)(Node.DuplicateFlags.Groups | Node.DuplicateFlags.Scripts | Node.DuplicateFlags.UseInstantiation));
            copy.Name = $"RingDraftReady{mode}";
            _small.AddChild(copy);
            copy.Scale = Vector2.One;
            try { copy.OverrideHotkeys(Array.Empty<string>()); } catch { /* the confirm key stays on Normal anyway */ }
            Place(copy, drop);
            drop += height * SmallScale * 0.95f;
            if (mode == RunMode.RingDraftPvP && copy.GetNodeOrNull<CanvasItem>("Image") is { } image)
                image.Material = Recolor(PvpRed);
            var m = mode;
            copy.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => ReadyWith(m)));
            AddLabel(copy, Labels[(int)mode], small: true);
            _copies.Add(copy);
        }
    }

    /// <summary>
    /// The holder sits so that a point at its bottom-right corner lands on the screen's bottom-right corner: the
    /// copies then keep their spots relative to that corner, just 60% of the distance away, through window resizes.
    /// </summary>
    private static void PlaceSmall()
    {
        if (_small is null || !GodotObject.IsInstanceValid(_small)) return;
        var screen = MegaCrit.Sts2.Core.Nodes.NGame.Instance?.Size ?? Vector2.Zero;
        var at = screen * (1 - SmallScale);
        if (_small.Position != at) _small.Position = at;
    }

    /// <summary>
    /// The button keeps its spot itself: it measures its offset from the bottom-right corner once, and slides between
    /// a shown and a hidden spot from there. A copy takes the game's button's offset, worked out for the 60% holder:
    /// same right edge, lower down.
    /// </summary>
    private static void Place(NConfirmButton button, float down)
    {
        var field = AccessTools.Field(typeof(NConfirmButton), "_posOffset");
        if (field?.GetValue(_normal) is Vector2 offset)
        {
            float width = _normal!.Size.X;
            field.SetValue(button, new Vector2(offset.X - width * (1 - SmallScale), offset.Y - down) / SmallScale);
            AccessTools.Method(typeof(NConfirmButton), "OnWindowChange")?.Invoke(button, null);
        }
        else
        {
            button.OffsetTop += down / SmallScale;
            button.OffsetBottom += down / SmallScale;
        }
    }

    /// <summary>The mode's name to the left of its button, in the game's gold title font, all the same size.</summary>
    private static Label AddLabel(NConfirmButton button, string text, bool small)
    {
        var label = new Label
        {
            Name = "RingDraftModeLabel", Text = text,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        StyleLike(label, 30);
        // Beside the button, not on it: the button's hover pulse scales everything on it.
        var parent = _normal!.GetParent();
        parent.AddChild(label);
        parent.MoveChild(label, _normal.GetIndex());
        _tags.Add(new Tag { Button = button, Label = label, Small = small });
        return label;
    }

    /// <summary>
    /// Every frame: each name follows its button's resting spot (as it slides, never as it pulses), and fades out as
    /// the button leaves (and back in as it returns), at the button's own speed.
    /// </summary>
    private static void UpdateTags()
    {
        PlaceSmall();
        foreach (var t in _tags)
        {
            if (!GodotObject.IsInstanceValid(t.Button) || !GodotObject.IsInstanceValid(t.Label)) continue;
            float s = t.Small ? SmallScale : 1f;
            var origin = t.Small && _small is not null ? _small.Position : Vector2.Zero;
            var pos = origin + t.Button.Position * s; // Position ignores the pulse; the scale lives on the button
            float h = (t.Button.Size.Y > 0 ? t.Button.Size.Y : 110) * s;
            t.Label.Size = new Vector2(320, h);
            // 12 px clear of the button's picture (which sits a little inside the button's box).
            float imageLeft = t.Button.GetNodeOrNull<Control>("Image") is { } image ? Math.Max(0, image.Position.X) : 10;
            t.Label.Position = new Vector2(pos.X + imageLeft * s - 12 - 320, pos.Y);

            bool shown = t.Button.IsEnabled;
            if (t.Shown == shown) continue;
            bool first = t.Shown is null;
            t.Shown = shown;
            t.Fade?.Kill();
            float alpha = shown ? 1f : 0f;
            if (first)
            {
                t.Label.Modulate = new Color(1, 1, 1, alpha);
                continue;
            }
            // Same as the button: 0.35 s, eased out (expo leaving, back coming in).
            t.Fade = t.Label.CreateTween();
            t.Fade.TweenProperty(t.Label, "modulate:a", alpha, 0.35)
                .SetEase(Tween.EaseType.Out)
                .SetTrans(shown ? Tween.TransitionType.Back : Tween.TransitionType.Expo);
        }
    }

    private static void StyleLike(Label label, int size)
    {
        try
        {
            if (GD.Load<Font>("res://themes/kreon_bold_glyph_space_two.tres") is { } font) label.AddThemeFontOverride("font", font);
        }
        catch { /* default font */ }
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(0.937f, 0.784f, 0.318f));
        label.AddThemeColorOverride("font_outline_color", new Color(0.098f, 0.161f, 0.188f));
        label.AddThemeConstantOverride("outline_size", 12);
        label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.125f));
        label.AddThemeConstantOverride("shadow_offset_x", 5);
        label.AddThemeConstantOverride("shadow_offset_y", 4);
    }

    /// <summary>Repaints the button's slate disc in a colour, keeping its shading; the tick on top is untouched.</summary>
    private static ShaderMaterial Recolor(Color tint)
    {
        var shader = new Shader
        {
            Code = """
                shader_type canvas_item;
                uniform vec4 tint : source_color;
                void fragment() {
                    vec4 c = texture(TEXTURE, UV) * COLOR;
                    float l = dot(c.rgb, vec3(0.299, 0.587, 0.114));
                    COLOR = vec4(tint.rgb * (0.55 + l * 1.5), c.a); // a clear red even where the slate is dark
                }
                """,
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("tint", tint);
        return material;
    }

    private static void ReadyWith(RunMode mode)
    {
        if (_screen is null || _normal is null || !_normal.IsEnabled) return;
        Pick(mode);
        try { AccessTools.Method(typeof(NCharacterSelectScreen), "OnEmbarkPressed")?.Invoke(_screen, new object[] { _normal }); }
        catch (Exception ex) { Log.Error($"Readying up for {mode} failed", ex); }
    }

    private static void Pick(RunMode mode)
    {
        HostMode = mode;
        _checkBegin = true;
        Send(mode);
        Log.Info($"Lobby: host picked {mode}");
    }

    private static void Send(RunMode mode)
    {
        if (_net is null) return;
        try { _net.SendMessage(new RingDraftMessage { payload = Codec.ToBase64(new ModeMsg(ModeMsg.LobbyVote, _net.NetId, 0, mode)) }); }
        catch (Exception ex) { Log.Error("Sending the lobby mode failed", ex); }
    }

    // May arrive off the main thread; handled in Step.
    private static void OnMessage(RingDraftMessage msg, ulong sender)
    {
        if (Codec.TryFromBase64(msg.payload, out var env) && env is ModeMsg m) Incoming.Enqueue((m, sender));
    }
}

/// <summary>Remembers each run's mode by its seed, so a continued run keeps it.</summary>
internal static class RunModes
{
    private static readonly string File = Path.Combine(OS.GetUserDataDir(), "ringdraft_runs.json");
    private static string? _seed;
    private static double _since;

    /// <summary>The mode of the run in progress; null while a client is still waiting to hear it from the host.</summary>
    public static RunMode? Current { get; private set; }

    public static void Begin(string seed, RunMode mode)
    {
        _seed = seed;
        Current = mode;
        Save(seed, mode);
        Log.Info($"Run {seed} is {mode}");
    }

    /// <summary>On joining a run in progress (new or continued): what this client remembers about it.</summary>
    public static void Attach(double now)
    {
        string? seed = CurrentSeed();
        _since = now;
        if (seed is null || seed == _seed) return;
        _seed = seed;
        Current = Load(seed);
        Log.Info(Current is { } m ? $"Run {seed} is {m}" : $"Run {seed}: mode not known here yet");
    }

    /// <summary>The host said which mode this run is in.</summary>
    public static void Announced(RunMode mode)
    {
        if (Current == mode) return;
        Current = mode;
        if (CurrentSeed() is { } seed)
        {
            _seed = seed;
            Save(seed, mode);
        }
        Log.Info($"Host says this run is {mode}");
    }

    /// <summary>
    /// The mode to play by. A run nobody remembers (started before modes existed) is Ring Draft, as it was then; a client
    /// waits a few seconds for the host's word before assuming that.
    /// </summary>
    public static RunMode? Effective(bool isHost, double now)
    {
        if (Current is { } m) return m;
        if (isHost || now - _since > 8)
        {
            Current = RunMode.RingDraft;
            return Current;
        }
        return null;
    }

    public static void Reset()
    {
        _seed = null;
        Current = null;
    }

    private static string? CurrentSeed()
    {
        try { return RunManager.Instance?.DebugOnlyGetState()?.Rng?.StringSeed; }
        catch { return null; }
    }

    private static Dictionary<string, RunMode> Read()
    {
        try
        {
            if (System.IO.File.Exists(File))
                return JsonSerializer.Deserialize<Dictionary<string, RunMode>>(System.IO.File.ReadAllText(File)) ?? new();
        }
        catch (Exception ex) { Log.Warn($"Couldn't read {File}: {ex.Message}"); }
        return new();
    }

    private static RunMode? Load(string seed) => Read().TryGetValue(seed, out var m) ? m : null;

    private static void Save(string seed, RunMode mode)
    {
        try
        {
            var all = Read();
            all[seed] = mode;
            // Keep the file small: the most recent runs are the ones that can still be continued.
            while (all.Count > 40) all.Remove(all.Keys.First());
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log.Warn($"Couldn't save the run mode: {ex.Message}"); }
    }
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsHost))]
internal static class LobbyHostPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) => LobbyModes.Attach(__instance);
}

[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.InitializeMultiplayerAsClient))]
internal static class LobbyClientPatch
{
    private static void Postfix(NCharacterSelectScreen __instance) => LobbyModes.Attach(__instance);
}

/// <summary>The run never starts before the host has picked its mode.</summary>
[HarmonyPatch(typeof(StartRunLobby), "BeginRunForAllPlayersIfAllReady")]
internal static class LobbyBeginPatch
{
    private static bool Prefix(StartRunLobby __instance)
    {
        // The host readies through one of the mode buttons, which picks the mode first; this only waits out the
        // moment in between.
        if (!LobbyModes.IsOurLobby(__instance) || !LobbyModes.LocalIsHost) return true;
        return LobbyModes.HostHasPicked;
    }
}

/// <summary>Everyone (host and clients) records the agreed mode as the run begins.</summary>
[HarmonyPatch(typeof(NCharacterSelectScreen), nameof(NCharacterSelectScreen.BeginRun))]
internal static class LobbyBeganPatch
{
    private static void Prefix(NCharacterSelectScreen __instance, string __0)
    {
        Pvp.Reset();
        if (__instance.Lobby is not { } lobby || !LobbyModes.IsOurLobby(lobby))
        {
            RunModes.Reset(); // a single player run, or one we didn't see the lobby for: no mode carried over
            return;
        }
        // A client that somehow missed the host's pick hears it again from the host once the run is under way.
        if (LobbyModes.HostMode is { } mode) RunModes.Begin(__0, mode);
        else RunModes.Reset();
    }
}
