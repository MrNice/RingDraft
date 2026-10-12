using System.Reflection;
using Godot;
using HarmonyLib;
using RingDraft.Core;

namespace RingDraft;

/// <summary>
/// Remembers when a potion was used or a card was played, so an in-combat "choose 1 of 3"
/// can be attributed to a potion (delegated), a Toolbox-style relic at combat start (delegated),
/// or a card effect (owner keeps it by default).
/// </summary>
internal static class SourceTracker
{
    public static double? LastPotionUse;
    public static double? LastCardPlay;
    public static double? CombatStart;

    public static double Now() => Time.GetTicksMsec() / 1000.0;

    public static ChoiceSource Infer(double now) =>
        DelegationRules.InferSource(now, LastPotionUse, LastCardPlay, CombatStart);

    public static void Reset() => LastPotionUse = LastCardPlay = CombatStart = null;

    /// <summary>
    /// First concrete method found, trying names in order. The game uses *Wrapper names for its
    /// non-virtual entry points (e.g. OnTryPurchaseWrapper), so those go first.
    /// VERIFY these against a decompile; the log says which one was patched or that none was found.
    /// </summary>
    public static IEnumerable<MethodBase> Find(string typeName, params string[] methodNames)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            Log.Warn($"Source tracking: type {typeName} not found");
            yield break;
        }
        foreach (var name in methodNames)
        {
            var matches = AccessTools.GetDeclaredMethods(type)
                .Where(m => m.Name == name && !m.IsAbstract && !m.IsGenericMethodDefinition)
                .ToList();
            if (matches.Count == 0) continue;
            foreach (var m in matches) yield return m;
            yield break;
        }
        Log.Warn($"Source tracking: none of [{string.Join(", ", methodNames)}] on {typeName}; these choices will count as Unknown");
    }
}

[HarmonyPatch]
internal static class PotionUsePatch
{
    private static IEnumerable<MethodBase> Targets() =>
        SourceTracker.Find("MegaCrit.Sts2.Core.Models.PotionModel", "OnUseWrapper", "OnUse", "Use");

    private static bool Prepare() => Targets().Any();
    private static IEnumerable<MethodBase> TargetMethods() => Targets();
    private static void Prefix() => SourceTracker.LastPotionUse = SourceTracker.Now();
}

[HarmonyPatch]
internal static class CardPlayPatch
{
    private static IEnumerable<MethodBase> Targets() =>
        SourceTracker.Find("MegaCrit.Sts2.Core.Models.CardModel", "OnPlayWrapper", "OnPlay");

    private static bool Prepare() => Targets().Any();
    private static IEnumerable<MethodBase> TargetMethods() => Targets();
    private static void Prefix() => SourceTracker.LastCardPlay = SourceTracker.Now();
}
