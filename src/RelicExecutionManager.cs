using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace DeckTracker;

public static class RelicExecutionManager
{
    // Holds the Class Name of the Relic currently executing its action
    public static readonly AsyncLocal<string?> ExecutingRelicId = new();

    // A generic Prefix that grabs the Relic's class name right before it fires
    public static void GenericRelicPrefix(RelicModel __instance)
    {
        ExecutingRelicId.Value = __instance.GetType().Name;
    }

    // A generic Postfix that clears it after the task finishes
    public static void GenericRelicPostfix(ref Task __result)
    {
        var capturedRelicId = ExecutingRelicId.Value;
        
        // Wrap the task to clear the async local ONLY after the damage is done
        async Task WrappedTask(Task originalTask)
        {
            try { await originalTask; }
            finally { ExecutingRelicId.Value = null; }
        }
        
        __result = WrappedTask(__result);
    }

    // Call this from your ModEntry.Initialize()
    public static void PatchAllDirectDamageRelics(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(GenericRelicPrefix)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(GenericRelicPostfix)));

        // Just add the Type and Method name for any Relic that deals direct damage!
        var relicMethodsToPatch = new (Type, string)[]
        {
            (typeof(MercuryHourglass), nameof(MercuryHourglass.AfterPlayerTurnStart)),
            (typeof(FestivePopper), nameof(FestivePopper.AfterPlayerTurnStart)),
            (typeof(MrStruggles), nameof(MrStruggles.AfterPlayerTurnStart)),
            
            (typeof(Kusarigama), nameof(Kusarigama.AfterCardPlayed)),
            (typeof(LetterOpener), nameof(LetterOpener.AfterCardPlayed)),
            (typeof(LostWisp), nameof(LostWisp.AfterCardPlayed)),
            
            (typeof(Tingsha), nameof(Tingsha.AfterCardDiscarded)),
            
            (typeof(CharonsAshes), nameof(CharonsAshes.AfterCardExhausted)),
            (typeof(ForgottenSoul), nameof(ForgottenSoul.AfterCardExhausted)),
            
            (typeof(Metronome), nameof(Metronome.AfterOrbChanneled)),
            
            (typeof(ParryingShield), nameof(ParryingShield.AfterSideTurnEnd)),
            
            (typeof(StoneCalendar), nameof(StoneCalendar.BeforeSideTurnEnd)),
            (typeof(ScreamingFlagon), nameof(ScreamingFlagon.BeforeSideTurnEnd)),
            
            
            
            
            // (typeof(LetterOpener), nameof(LetterOpener.AfterCardPlayed)),
            // Add Charon's Ashes, Tingsha, etc. here!
        };

        foreach (var (relicType, methodName) in relicMethodsToPatch)
        {
            var originalMethod = AccessTools.Method(relicType, methodName);
            if (originalMethod != null)
            {
                harmony.Patch(originalMethod, prefix: prefix, postfix: postfix);
                Godot.GD.Print($"[DeckTracker] Dynamically Patched Direct Damage Relic: {relicType.Name}");
            }
        }
    }
}