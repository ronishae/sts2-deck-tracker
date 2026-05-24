using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Relics;

namespace DeckTracker;

public static class RelicExecutionManager
{
    // Holds the Class Name of the Relic currently executing its action
    public static readonly AsyncLocal<string?> ExecutingRelicId = new();
    
    // --- STATE MANAGEMENT ---
    
    public static void ResetState()
    {
        ExecutingRelicId.Value = null;
        
        // Clear and nullify the pending power modifiers
        PendingPowerModifiers.Value?.Clear();

        // Clear and nullify the pending orb modifiers
        PendingOrbModifiers.Value?.Clear();

        Godot.GD.Print("[DeckTracker] RelicExecutionManager state fully reset.");
    }
    
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

    // Stores (Relic Class Name, Delta Amount, Power Type)
    public static readonly AsyncLocal<List<(string relicId, decimal delta, string powerType)>> PendingPowerModifiers = new();

    // A Universal Hook for ANY relic that modifies incoming powers (Ruined Helmet)
    public static void TryModifyPowerAmountReceivedPostfix(RelicModel __instance, PowerModel canonicalPower, Creature target, decimal amount, Creature? applier, ref decimal modifiedAmount, ref bool __result)
    {
        // If the relic successfully changed the amount...
        if (__result && modifiedAmount != amount)
        {
            PendingPowerModifiers.Value ??= new();
            PendingPowerModifiers.Value.Add((
                __instance.GetType().Name, 
                modifiedAmount - amount, 
                canonicalPower.GetType().Name
            ));
            
            Godot.GD.Print($"[DeckTracker] Relic Modifier Intercepted: {__instance.GetType().Name} added {modifiedAmount - amount} to {canonicalPower.GetType().Name}");
        }
    }
    
    // For Infused Core's + 1 Damage to Lightning Orbs
    public static readonly AsyncLocal<List<(string relicId, decimal delta)>> PendingOrbModifiers = new();

    public static void ModifyOrbValuePostfix(RelicModel __instance, OrbModel orb, decimal value, ref decimal __result)
    {
        // CRITICAL GATE: Only track this if an orb is actively firing in combat!
        // This prevents the UI tooltips from flooding our math engine.
        if (CardRegistry.ExecutingOrb != null && __result != value)
        {
            PendingOrbModifiers.Value ??= new();
            PendingOrbModifiers.Value.Add((__instance.GetType().Name, __result - value));
        }
    }
    
    // Call this from your ModEntry.Initialize()
    public static void PatchAllDamageRelics(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(GenericRelicPrefix)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(GenericRelicPostfix)));

        // Just add the Type and Method name for any Relic that deals direct damage!
        var relicMethodsToPatch = new (Type, string)[]
        {
            // Direct Damage
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
            
            // Strength Relics (Ruined Helmet is handled separately)
            (typeof(Vajra), nameof(Vajra.AfterRoomEntered)),
            (typeof(RedSkull), nameof(RedSkull.AfterRoomEntered)),
            (typeof(RedSkull), nameof(RedSkull.AfterCurrentHpChanged)),
            (typeof(ReptileTrinket), nameof(ReptileTrinket.AfterPotionUsed)),
            (typeof(SparklingRouge), nameof(SparklingRouge.AfterBlockCleared)),
            (typeof(Girya), nameof(Girya.AfterRoomEntered)),
            (typeof(Shuriken), nameof(Shuriken.AfterCardPlayed)),
            (typeof(MiniRegent), nameof(MiniRegent.AfterStarsSpent)),
            (typeof(SlingOfCourage), nameof(SlingOfCourage.AfterRoomEntered)),
            (typeof(Brimstone), nameof(Brimstone.AfterSideTurnStart)),
            (typeof(ToastyMittens), nameof(ToastyMittens.BeforeHandDraw)),
            (typeof(EmberTea),  nameof(EmberTea.AfterRoomEntered)),
            (typeof(SwordOfJade), nameof(SwordOfJade.AfterRoomEntered)),
            
            // Orbs
            (typeof(DataDisk), nameof(DataDisk.AfterRoomEntered)),
            (typeof(CrackedCore), nameof(CrackedCore.BeforeSideTurnStart)),
            (typeof(InfusedCore), nameof(InfusedCore.AfterSideTurnStart)),
            (typeof(SymbioticVirus), nameof(SymbioticVirus.AfterSideTurnStart)),
            (typeof(EmotionChip), nameof(EmotionChip.AfterPlayerTurnStart)),
            
            // Vigor
            (typeof(Akabeko), nameof(Akabeko.AfterSideTurnStart)),
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