using Godot;
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

        // Clear and nullify the pending orb modifiers
        PendingOrbModifiers.Value?.Clear();

        Log.Debug("RelicExecutionManager state fully reset.");
    }
    
    // A generic Prefix that grabs the Relic's class name right before it fires
    public static void GenericRelicPrefix(RelicModel __instance)
    {
        ExecutingRelicId.Value = __instance.Id.Entry;
        CardRegistry.RelicNameCache[__instance.Id.Entry] = __instance.Title.GetFormattedText();
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
    
    public static void TryModifyPowerAmountReceivedPostfix(RelicModel __instance, PowerModel canonicalPower, Creature target, decimal amount, Creature? applier, ref decimal modifiedAmount, ref bool __result)
    {
        if (__result && modifiedAmount > amount)
        {
            CardRegistry.RelicNameCache[__instance.Id.Entry] = __instance.Title.GetFormattedText();
            var delta = modifiedAmount - amount;
            var relicId = "RELIC_" + __instance.Id.Entry;
            var powerId = canonicalPower.Id.Entry ?? "";

            Log.Debug($"TryModifyPowerAmountReceivedPostfix. {relicId} intercepted! Directly adding {delta} {powerId} to ledger.");

            // Directly inject the relic's contribution into the correct ledger!
            if (powerId == "STRENGTH_POWER")
            {
                CardRegistry.AddPersistentBuffById(powerId, delta, relicId);
                return;
            }
            Log.Warn("TryModifyPowerAmountReceivedPostfix. Unsupported power amount modification.");
        }
    }

    public static void ModifyPowerAmountGivenPostfix(RelicModel __instance, PowerModel power, Creature giver, decimal amount, Creature? target, CardModel? cardSource, ref decimal __result)
    {
        var relicId = "RELIC_" + __instance.Id.Entry;
        var powerId = power.Id.Entry ?? "";
        Log.Debug($"ModifyPowerAmountGivenPostfix. RelicId: {relicId}, PowerId: {powerId}, Amount: {amount}, Result: {__result}");

        // __result is the additive contribution this relic returns, not the accumulated total.
        // amount is the base input — comparing __result > amount would fail when the bonus (e.g. 1) is less than the base (e.g. 3).
        if (__result > 0)
        {
            CardRegistry.RelicNameCache[__instance.Id.Entry] = __instance.Title.GetFormattedText();
            var delta = __result;

            if (powerId == "POISON_POWER" && target != null)
            {
                // ModifyPowerAmountGivenAdditive fires during tooltip/targeting preview as well as on real application.
                // Only track during an active card play to avoid inflating relic poison shares before any power is applied.
                if (!CardRegistry.IsCardPlayActive())
                {
                    Log.Debug($"ModifyPowerAmountGivenPostfix. Skipping {relicId} — no active card play (preview/tooltip).");
                    return;
                }

                CardRegistry.AddPoisonSharesById(target, delta, relicId);
                Log.Debug($"ModifyPowerAmountGivenPostfix. {relicId} intercepted! Directly adding {delta} {powerId} to ledger.");
            }
            else
            {
                Log.Warn($"ModifyPowerAmountGivenPostfix. Unsupported power amount modification. PowerId: {powerId}");
            }
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
            PendingOrbModifiers.Value.Add((__instance.Id.Entry, __result - value));
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
            
            // Thorns
            (typeof(BronzeScales), nameof(BronzeScales.AfterRoomEntered)),
            
            // Poison
            (typeof(TwistedFunnel), nameof(TwistedFunnel.BeforeSideTurnStart)),
            
            // Forge
            (typeof(FencingManual), nameof(FencingManual.AfterSideTurnStart)),
            
            // Card Gen
            (typeof(NinjaScroll), nameof(NinjaScroll.BeforeHandDraw)),
            (typeof(VexingPuzzlebox), nameof(VexingPuzzlebox.AfterPlayerTurnStart)),
            (typeof(BurningSticks), nameof(BurningSticks.AfterCardExhausted)),
            (typeof(Toolbox), nameof(Toolbox.BeforeHandDraw)),
            (typeof(Crossbow), nameof(Crossbow.AfterSideTurnStart)),
            (typeof(ChoicesParadox), nameof(ChoicesParadox.AfterPlayerTurnStart)),
            (typeof(MusicBox), nameof(MusicBox.AfterCardPlayed)),
        };

        foreach (var (relicType, methodName) in relicMethodsToPatch)
        {
            var originalMethod = AccessTools.Method(relicType, methodName);
            if (originalMethod != null)
            {
                harmony.Patch(originalMethod, prefix: prefix, postfix: postfix);
                Log.Debug($"Dynamically Patched Direct Damage Relic: {relicType.Name}");
            }
        }
    }
}