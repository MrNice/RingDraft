using Godot;
using HarmonyLib;
using MegaCrit.Sts2.addons.mega_text;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Audio;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Ring Draft PvP. The pickers try to sink each other's decks, and the moment only one player is left standing the
/// match is over: they get the Victory screen, everyone else gets Defeat, and the run ends, even mid-fight. Players who
/// fall during the same enemy turn fall together (if nobody is left, everyone loses). A player who leaves counts as
/// defeated. Nothing about a PvP run counts towards progress, and card reward skips are limited to one every 3 floors.
///
/// The decision is made where every client's game is in the same state (end of a turn, end of a fight), so everyone
/// agrees without waiting; the host also decides the in-between cases and tells everyone (<see cref="PvpEndMsg"/>).
/// </summary>
internal static class Pvp
{
    private const double ForfeitAfterSeconds = 15;
    private const double SettleSeconds = 1.0;

    public static readonly SkipCharges Skips = new();

    /// <summary>The match in this run is over. Tied to the run, so the next run starts fresh.</summary>
    public static bool Ended => _endedSeed is not null && _endedSeed == CurrentSeed();
    public static bool LocalWon { get; private set; }
    /// <summary>Nobody was left standing.</summary>
    public static bool Tied { get; private set; }

    /// <summary>The host's word, for clients: set by the controller.</summary>
    public static Action<ulong>? Announce { get; set; }

    private static readonly HashSet<ulong> Forfeited = new();
    private static readonly Dictionary<ulong, double> GoneSince = new();
    // Which turn each fallen player fell on (counting every turn end, both sides), to tell who was in a tie.
    private static readonly Dictionary<ulong, int> FellOnTurn = new();
    private static int _turn, _endTurn;
    private static CombatManager? _subscribed;
    private static string? _seed, _endedSeed;
    private static double? _settlingSince;
    private static bool _lossPending;

    /// <summary>This is a PvP co-op run with at least two players.</summary>
    public static bool InPvpRun
    {
        get
        {
            if (RunModes.Current != RunMode.RingDraftPvP) return false;
            try
            {
                var type = RunManager.Instance?.NetService?.Type;
                if (type is not (NetGameType.Host or NetGameType.Client)) return false;
                return (RunManager.Instance?.DebugOnlyGetState()?.Players.Count ?? 0) >= 2;
            }
            catch { return false; }
        }
    }

    public static int Floor() => (int)SmithHold.Floor();

    private static string? CurrentSeed()
    {
        try { return RunManager.Instance?.DebugOnlyGetState()?.Rng?.StringSeed; }
        catch { return null; }
    }

    /// <summary>Forgets everything about the last match (a new run started).</summary>
    public static void Reset()
    {
        _seed = CurrentSeed();
        _endedSeed = null;
        LocalWon = false;
        Tied = false;
        Forfeited.Clear();
        GoneSince.Clear();
        FellOnTurn.Clear();
        _turn = _endTurn = 0;
        Skips.Reset();
        _settlingSince = null;
        _lossPending = false;
    }

    /// <summary>Every frame while a PvP run is in progress.</summary>
    public static void Step(double now, bool isHost)
    {
        if (CurrentSeed() is { } seed && seed != _seed)
        {
            Reset();
            _seed = seed;
        }
        if (_subscribed != CombatManager.Instance)
        {
            if (_subscribed is not null) _subscribed.TurnEnded -= OnTurnEnded;
            _subscribed = CombatManager.Instance;
            if (_subscribed is not null) _subscribed.TurnEnded += OnTurnEnded;
        }

        if (Ended)
        {
            // Ending mid-fight leaves the loss waiting for the next action; make sure the fight actually stops.
            if (_lossPending && CombatManager.Instance is { IsAboutToLose: true } cm)
            {
                _lossPending = false;
                try { AccessTools.Method(typeof(CombatManager), "ProcessPendingLoss")?.Invoke(cm, null); }
                catch (Exception ex) { Log.Warn($"Stopping the fight failed: {ex.Message}"); }
            }
            return;
        }
        if (!InPvpRun || RunManager.Instance?.IsGameOver == true) return;
        RecordFalls();
        if (!isHost) return;

        TrackLeavers(now);

        // Mid enemy turn, wait for the turn to end: everyone who falls during it falls together.
        var cm2 = CombatManager.Instance;
        bool inCombat = cm2 is { IsInProgress: true };
        if (inCombat && (cm2!.IsEnemyTurnStarted || cm2.DebugOnlyGetState()?.CurrentSide == CombatSide.Enemy))
        {
            _settlingSince = null;
            return;
        }

        if (Victor() is null)
        {
            _settlingSince = null;
            return;
        }
        // Give a moment for anything resolving at the same time (start-of-turn damage hits everyone in turn).
        _settlingSince ??= now;
        if (now - _settlingSince < SettleSeconds) return;
        Decide("last one standing");
    }

    /// <summary>The end of a fight: decided before the game revives anyone who fell.</summary>
    public static bool BlockCombatEnd()
    {
        if (!Ended && InPvpRun && RunManager.Instance?.IsGameOver != true) Decide("end of the fight");
        return Ended;
    }

    /// <summary>The host said the match is over.</summary>
    public static void Heard(ulong winner)
    {
        if (Ended || !InPvpRun) return;
        Log.Info($"Host says the match is over: {(winner == 0 ? "nobody" : winner.ToString())} won");
        End(winner);
    }

    /// <summary>The host left the run on purpose while only two were playing: the one left wins.</summary>
    public static bool HostLeft(NetErrorInfo info)
    {
        if (Ended || !InPvpRun) return false;
        try
        {
            var reason = info.GetReason();
            if (reason is not (NetError.Quit or NetError.QuitGameOver)) return false;
            var state = RunManager.Instance?.DebugOnlyGetState();
            var me = GameQuery.Me();
            if (state is null || me is null || state.Players.Count != 2 || me.Creature.IsDead) return false;
            Log.Info("The other player left the match");
            End(me.NetId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Handling the host leaving failed", ex);
            return false;
        }
    }

    private static void OnTurnEnded(CombatState _)
    {
        try
        {
            if (!Ended && InPvpRun && RunManager.Instance?.IsGameOver != true)
            {
                RecordFalls();
                Decide("end of the turn");
            }
        }
        catch (Exception ex) { Log.Error("PvP turn check failed", ex); }
        _turn++;
    }

    /// <summary>Notes the turn each player fell on; a player revived at the end of a fight starts over.</summary>
    private static void RecordFalls()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null) return;
        foreach (var p in state.Players)
        {
            if (!p.Creature.IsDead) FellOnTurn.Remove(p.NetId);
            else FellOnTurn.TryAdd(p.NetId, _turn);
        }
    }

    private static ulong? Victor()
    {
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (state is null) return null;
        return PvpRules.Victor(state.Players
            .Select(p => (p.NetId, Alive: !p.Creature.IsDead && !Forfeited.Contains(p.NetId)))
            .ToList());
    }

    private static void Decide(string when)
    {
        if (Victor() is not { } winner) return;
        Log.Info($"PvP decided at the {when}: {(winner == 0 ? "nobody" : winner.ToString())} won");
        Announce?.Invoke(winner);
        End(winner);
    }

    /// <summary>The host marks a player who has been gone a while as defeated.</summary>
    private static void TrackLeavers(double now)
    {
        var lobby = RunManager.Instance?.RunLobby;
        var state = RunManager.Instance?.DebugOnlyGetState();
        if (lobby is null || state is null) return;
        var connected = lobby.ConnectedPlayerIds;
        ulong me = GameQuery.Me()?.NetId ?? 0;
        foreach (var p in state.Players)
        {
            if (p.NetId == me || Forfeited.Contains(p.NetId)) continue;
            if (connected.Contains(p.NetId))
            {
                GoneSince.Remove(p.NetId);
                continue;
            }
            if (!GoneSince.TryGetValue(p.NetId, out var since)) GoneSince[p.NetId] = since = now;
            if (now - since >= ForfeitAfterSeconds)
            {
                Forfeited.Add(p.NetId);
                Log.Info($"Player {p.NetId} left the match and counts as defeated");
            }
        }
    }

    private static void End(ulong winner)
    {
        if (Ended) return;
        _endedSeed = CurrentSeed() ?? "";
        _endTurn = _turn;
        ulong me = GameQuery.Me()?.NetId ?? 0;
        LocalWon = winner != 0 && winner == me;
        Tied = winner == 0;
        Log.Info(Tied ? "PvP match tied: nobody won" : LocalWon ? "You won the PvP match" : "You lost the PvP match");

        try
        {
            if (CombatManager.Instance is { IsInProgress: true } cm)
            {
                cm.LoseCombat();
                _lossPending = true;
            }
        }
        catch (Exception ex) { Log.Warn($"Stopping the fight failed: {ex.Message}"); }

        try { NRun.Instance?.RunMusicController?.StopMusic(); } catch { }
        try { if (!LocalWon) NAudioManager.Instance?.PlayMusic("event:/temp/sfx/game_over"); } catch { }

        try
        {
            var save = RunManager.Instance!.OnEnded(LocalWon);
            NRun.Instance?.ShowGameOverScreen(save);
        }
        catch (Exception ex) { Log.Error("Showing the end of the match failed", ex); }
    }

    /// <summary>
    /// The end screen of a PvP run: the winner's says Victory, with a gloat and nothing about the Architect; a
    /// tie (everyone fell on the same turn) says so, with a dig at everyone involved; anyone who fell a turn before the
    /// tie, or lost to the winner, keeps the game's Defeat banner with a dig of their own (see <see cref="PvpText"/>).
    /// </summary>
    internal static void DressGameOver(NGameOverScreen screen)
    {
        if (!Ended && !InPvpRun) return;
        var t = Traverse.Create(screen);
        // Nothing from a PvP run counts towards unlocks.
        t.Field("_score").SetValue(0);

        try
        {
            if (Ended && LocalWon) DressVictory(t);
            else if (Ended ? Tied : EveryoneFell()) DressTie(t, missedIt: !InTie());
            else if (Ended) DressLoss(t);
        }
        catch (Exception ex) { Log.Error("Dressing the PvP end screen failed", ex); }
    }

    /// <summary>The last players fell together and the game ended the run itself (before we could call it).</summary>
    private static bool EveryoneFell()
    {
        var players = RunManager.Instance?.DebugOnlyGetState()?.Players;
        return players is { Count: >= 2 } && players.All(p => p.Creature.IsDead);
    }

    private static void DressVictory(Traverse t)
    {
        if (t.Field("_history").GetValue() is RunHistory history) Traverse.Create(history).Property("Win").SetValue(true); // the screen's own copy
        SetBannerAndQuote(t, new LocString("game_over_screen", "BANNER.trueWin").GetRawText(), Any(Text().WinQuotes));
        if (t.Field("_victoryDamageLabel").GetValue() is MegaRichTextLabel damage) damage.Text = "";
    }

    /// <summary>Whether this player fell on the tie's final turn, rather than on an earlier one.</summary>
    private static bool InTie()
    {
        ulong me = GameQuery.Me()?.NetId ?? 0;
        int final = Ended ? _endTurn : _turn; // the game ended it itself: that's still the turn in progress
        return PvpRules.InTie(FellOnTurn.TryGetValue(me, out var turn) ? turn : null, final);
    }

    /// <summary>
    /// A tie: the ones who fell together get "Tie - No Winner" and a dig; anyone who fell on an earlier turn missed
    /// the tie, so they keep the game's Defeat banner, with a dig about that instead.
    /// </summary>
    private static void DressTie(Traverse t, bool missedIt)
    {
        var text = Text();
        if (missedIt) SetBannerAndQuote(t, null, Any(text.NearTieQuotes));
        else SetBannerAndQuote(t, text.TieBanner, Any(text.TieQuotes));
        Log.Info(missedIt ? "PvP tie: you fell a turn early and missed it" : "PvP tie screen");
    }

    /// <summary>Someone else won: the game's Defeat banner, with a dig about losing to them.</summary>
    private static void DressLoss(Traverse t)
    {
        SetBannerAndQuote(t, null, Any(Text().LossQuotes));
        Log.Info("PvP loss screen");
    }

    private static PvpText.Lines Text()
    {
        string language = "eng";
        try { language = LocManager.Instance?.Language ?? "eng"; } catch { }
        return PvpText.For(language);
    }

    private static string Any(IReadOnlyList<string> quotes) => quotes[(int)(GD.Randi() % (uint)quotes.Count)];

    /// <summary>The banner, and the quote under it (shown first, then kept, where a defeat would name the killer).</summary>
    private static void SetBannerAndQuote(Traverse t, string? banner, string quote)
    {
        if (banner is not null && t.Field("_banner").GetValue() is NCommonBanner b) b.label.SetTextAutoSize(banner);
        t.Field("_encounterQuote").SetValue(quote);
        if (t.Field("_deathQuote").GetValue() is MegaRichTextLabel q) q.Text = quote;
    }
}

/// <summary>PvP: a fight that ends with one player standing ends the match, before the game revives anyone.</summary>
[HarmonyPatch(typeof(CombatManager), "EndCombatInternal")]
internal static class PvpCombatEndPatch
{
    private static bool Prefix(ref Task __result)
    {
        try
        {
            if (!Pvp.BlockCombatEnd()) return true;
            __result = Task.CompletedTask;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("PvP combat end check failed", ex);
            return true;
        }
    }
}

/// <summary>PvP runs don't count: no progress, unlocks, achievements, run history or stats; the save is just removed.</summary>
[HarmonyPatch(typeof(RunManager), nameof(RunManager.OnEnded))]
internal static class PvpNoProgressPatch
{
    private static bool Prefix(RunManager __instance, ref SerializableRun __result)
    {
        if (!Pvp.InPvpRun && !Pvp.Ended) return true;
        try
        {
            AccessTools.Method(typeof(RunManager), "UpdatePlayerStatsInMapPointHistory")?.Invoke(__instance, null);
            __result = (SerializableRun)AccessTools.Method(typeof(RunManager), "ToSave")!.Invoke(__instance, new object?[] { null })!;
            var type = __instance.NetService?.Type;
            if (type == NetGameType.Singleplayer) SaveManager.Instance.DeleteCurrentRun();
            else if (type == NetGameType.Host) SaveManager.Instance.DeleteCurrentMultiplayerRun();
            Log.Info("PvP run ended; it doesn't count towards progress");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Ending the PvP run quietly failed; ending it normally", ex);
            return true;
        }
    }
}

[HarmonyPatch(typeof(NGameOverScreen), nameof(NGameOverScreen._Ready))]
internal static class PvpGameOverPatch
{
    private static void Postfix(NGameOverScreen __instance)
    {
        try { Pvp.DressGameOver(__instance); }
        catch (Exception ex) { Log.Error("PvP game over screen failed", ex); }
    }
}

/// <summary>No badges saved from a PvP run.</summary>
[HarmonyPatch(typeof(NGameOverScreen), "SaveBadgesToProgress")]
internal static class PvpNoBadgesPatch
{
    private static bool Prefix() => !(Pvp.Ended || Pvp.InPvpRun);
}

/// <summary>Once the match is over, the run is over for everyone, winner included.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.IsGameOver), MethodType.Getter)]
internal static class PvpGameOverStatePatch
{
    private static void Postfix(ref bool __result) => __result |= Pvp.Ended;
}

[HarmonyPatch(typeof(RunManager), nameof(RunManager.IsGameOver), MethodType.Getter)]
internal static class PvpRunGameOverPatch
{
    private static void Postfix(ref bool __result) => __result |= Pvp.Ended;
}

/// <summary>
/// Losing the connection after the match is over (someone went back to the menu) leaves you on your end screen; and
/// in a two-player match, the host quitting means the client wins.
/// </summary>
[HarmonyPatch(typeof(RunManager), "LocalPlayerDisconnected")]
internal static class PvpDisconnectPatch
{
    private static bool Prefix(NetErrorInfo __0)
    {
        if (Pvp.Ended) return false;
        return !Pvp.HostLeft(__0);
    }
}
