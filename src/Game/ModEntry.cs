using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;

namespace RingDraft;

[ModInitializer(nameof(Init))]
public static class ModEntry
{
    public static void Init()
    {
        Config.Load();

        // Patch each class on its own so one renamed game method can't take the whole mod down.
        var harmony = new Harmony("pixelicious.ringdraft");
        int ok = 0, skipped = 0;
        foreach (var type in typeof(ModEntry).Assembly.GetTypes())
        {
            if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
            try
            {
                var patched = harmony.CreateClassProcessor(type).Patch();
                if (patched is { Count: > 0 }) ok++; else skipped++;
            }
            catch (Exception ex)
            {
                skipped++;
                Log.Error($"Patch {type.Name} failed", ex);
            }
        }
        Log.Info($"Harmony: {ok} patch classes applied, {skipped} skipped");

        if (NGame.Instance is { } game)
            game.CallDeferred(Node.MethodName.AddChild, new RingDraftController());
        else
            Log.Error("NGame.Instance was null at init; Ring Draft is inactive");
    }
}
