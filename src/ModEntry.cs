using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Rewards;

namespace DeckTracker;

[ModInitializer("Initialize")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null) return;
        Log.Info("Initializing DeckTracker mod...");
        _harmony = new Harmony("com.roni.sts2.deck_tracker");
        
        var cleanUpMethod = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), nameof(MegaCrit.Sts2.Core.Runs.RunManager.CleanUp));
        var cleanUpPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RunManagerCleanUpPrefix)));
        _harmony.Patch(cleanUpMethod, prefix: cleanUpPrefix);

        // Reset the tracker whenever a brand-new run is set up (not a load/resume), so restarting on the same
        // seed wipes the previous run's data instead of resuming its stale save.
        var setUpNewRunPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SetUpNewRunPostfix)));
        _harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), nameof(MegaCrit.Sts2.Core.Runs.RunManager.SetUpNewSingleplayer)), postfix: setUpNewRunPostfix);
        _harmony.Patch(AccessTools.Method(typeof(MegaCrit.Sts2.Core.Runs.RunManager), nameof(MegaCrit.Sts2.Core.Runs.RunManager.SetUpNewMultiplayer)), postfix: setUpNewRunPostfix);

        // --- Potion Lifecycle ---
        PatchHook(nameof(Hook.AfterPotionProcured), nameof(HookPatches.AfterPotionProcuredPrefix));
        PatchHook(nameof(Hook.AfterPotionDiscarded), nameof(HookPatches.AfterPotionDiscardedPrefix));
        PatchHook(nameof(Hook.BeforePotionUsed), nameof(HookPatches.BeforePotionUsedPrefix));
        PatchHook(nameof(Hook.AfterPotionUsed), nameof(HookPatches.AfterPotionUsedPrefix));
        
        // --- Relic Lifecycle ---
        var afterObtainedMethod = AccessTools.Method(typeof(RelicModel), nameof(RelicModel.AfterObtained));
        _harmony.Patch(afterObtainedMethod, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RelicAfterObtainedPrefix))));

        var removeRelicMethod = AccessTools.Method(typeof(Player), nameof(Player.RemoveRelicInternal));
        _harmony.Patch(removeRelicMethod, postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PlayerRemoveRelicPostfix))));
        
        RelicExecutionManager.PatchAllDamageRelics(_harmony);
        
        // --- Core Power Modifiers ---
        _harmony.Patch(AccessTools.Method(typeof(RuinedHelmet), nameof(RuinedHelmet.TryModifyPowerAmountReceived)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(RelicExecutionManager.TryModifyPowerAmountReceivedPostfix))));
    
        _harmony.Patch(AccessTools.Method(typeof(InfusedCore), nameof(InfusedCore.ModifyOrbValue)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(RelicExecutionManager.ModifyOrbValuePostfix))));
        
        _harmony.Patch(AccessTools.Method(typeof(SneckoSkull), nameof(SneckoSkull.ModifyPowerAmountGivenAdditive)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(RelicExecutionManager), nameof(RelicExecutionManager.ModifyPowerAmountGivenPostfix))));
        
        // --- Core Lifecycle Hooks ---
        PatchHook(nameof(Hook.AfterRoomEntered), nameof(HookPatches.AfterRoomEnteredPostfix));
        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterSideTurnStart), nameof(HookPatches.AfterSideTurnStartPostfix));
        PatchHook(nameof(Hook.AfterCombatEnd), nameof(HookPatches.AfterCombatEndPostfix)); 
        PatchHook(nameof(Hook.BeforeRoomEntered), nameof(HookPatches.BeforeRoomEnteredPrefix));

        // --- Run Export Hooks ---
        PatchHook(nameof(Hook.AfterDamageReceived), nameof(HookPatches.AfterDamageReceivedPostfix));
        PatchHook(nameof(Hook.AfterPlayerTurnStart), nameof(HookPatches.AfterPlayerTurnStartPostfix));
        PatchHook(nameof(Hook.AfterDeath), nameof(HookPatches.AfterDeathPostfix));

        // --- Damage Hooks ---
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));
        PatchHook(nameof(Hook.AfterForge), nameof(HookPatches.AfterForgePostfix));
        PatchHook(nameof(Hook.BeforeCardRemoved), nameof(HookPatches.BeforeCardRemovedPostfix));
        PatchHook(nameof(Hook.AfterCardDrawn), nameof(HookPatches.AfterCardDrawnPostfix));
        PatchHook(nameof(Hook.AfterCardChangedPiles), nameof(HookPatches.AfterCardChangedPilesPostfix));
        PatchHook(nameof(Hook.BeforeCardPlayed), nameof(HookPatches.BeforeCardPlayedPostfix));
        PatchHook(nameof(Hook.BeforeCardAutoPlayed), nameof(HookPatches.BeforeCardAutoPlayedPostfix));
        PatchHook(nameof(Hook.AfterCardPlayed), nameof(HookPatches.AfterCardPlayedPostfix));
        PatchHook(nameof(Hook.BeforePowerAmountChanged), nameof(HookPatches.BeforePowerAmountChangedPostfix));
        PatchHook(nameof(Hook.ModifyDamage), nameof(HookPatches.ModifyDamagePostfix));
        PatchHook(nameof(Hook.AfterDiedToDoom), nameof(HookPatches.AfterDiedToDoomPostfix));

        // --- UNIQUE POWER HOOKS ---
        _harmony.Patch(AccessTools.Method(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart)), 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPostfix))));
        
        _harmony.Patch(AccessTools.Method(typeof(DoomPower), nameof(DoomPower.DoomKill)), 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DoomKillPrefix))));
        
        _harmony.Patch(AccessTools.Method(typeof(CountdownPower), nameof(CountdownPower.AfterSideTurnStart)), 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(ReaperFormPower), nameof(ReaperFormPower.AfterDamageGiven)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReaperFormAfterDamageGivenPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReaperFormAfterDamageGivenPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(NecroMasteryPower), nameof(NecroMasteryPower.AfterCurrentHpChanged)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.NecroMasteryAfterCurrentHpChangedPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.NecroMasteryAfterCurrentHpChangedPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Remove), new[] { typeof(PowerModel) }),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BeforePowerRemovedPrefix))));

        _harmony.Patch(AccessTools.Method(typeof(CreatureCmd), nameof(CreatureCmd.LoseBlock)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoseBlockPrefix))));

        _harmony.Patch(AccessTools.Method(typeof(LoopPower), nameof(LoopPower.AfterPlayerTurnStart)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(RollingBoulderPower), nameof(RollingBoulderPower.AfterPlayerTurnStart)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RollingBoulderAfterPlayerTurnStartPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RollingBoulderAfterPlayerTurnStartPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(TheBombPower), nameof(TheBombPower.BeforeSideTurnEnd)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TheBombBeforeSideTurnEndPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TheBombBeforeSideTurnEndPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(PanachePower), nameof(PanachePower.AfterCardPlayed)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PanacheAfterCardPlayedPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PanacheAfterCardPlayedPostfix))));

        // Powers that create cards (Infinite Blades, Spectrum Shift, ...) are wired from a single list so
        // the cards they generate attribute back to the card that applied the power.
        CardGeneratingPowerManager.PatchAll(_harmony);

        _harmony.Patch(AccessTools.Method(typeof(PrepTimePower), nameof(PrepTimePower.AfterSideTurnStart)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PrepTimePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PrepTimePostfix))));

        _harmony.Patch(AccessTools.Method(typeof(RitualPower), nameof(RitualPower.AfterSideTurnEnd)), 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RitualPowerTurnEndPrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RitualPowerTurnEndPostfix))));

        _harmony.Patch(AccessTools.Method(typeof(HandDrill), nameof(HandDrill.AfterDamageGiven)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HandDrillAfterDamagePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HandDrillAfterDamagePostfix))));

        _harmony.Patch(AccessTools.Method(typeof(GoldPlatedCables), nameof(GoldPlatedCables.ModifyOrbPassiveTriggerCounts)),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.GoldPlatedCablesModifyOrbPassivePostfix))));
        
        _harmony.Patch(AccessTools.Method(typeof(TheBoot), nameof(TheBoot.ModifyHpLostAfterOstyLate)), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TheBootModifyHpPostfix))));

        // --- UNIFIED DYNAMIC PATCHING ---
        
        // 1. Simple FIFO Damage Trackers
        var simpleMethods = new (Type, string)[] {
            (typeof(FlameBarrierPower), nameof(FlameBarrierPower.AfterDamageReceived)),
            (typeof(JuggernautPower), nameof(JuggernautPower.AfterBlockGained)),
            (typeof(HauntPower), nameof(HauntPower.AfterCardPlayed)),
            (typeof(SpeedsterPower), nameof(SpeedsterPower.AfterCardDrawn)),
            (typeof(ThunderPower), nameof(ThunderPower.AfterOrbEvoked)),
            (typeof(HailstormPower), nameof(HailstormPower.BeforeSideTurnEnd)),
            (typeof(ThornsPower), nameof(ThornsPower.BeforeDamageReceived)),
            (typeof(SerpentFormPower), nameof(SerpentFormPower.AfterCardPlayed)),
            (typeof(BlackHolePower), nameof(BlackHolePower.AfterCardPlayed)),
            (typeof(BlackHolePower), nameof(BlackHolePower.AfterStarsGained)),
            (typeof(SleightOfFleshPower), nameof(SleightOfFleshPower.AfterPowerAmountChanged)),
            (typeof(InfernoPower), nameof(InfernoPower.AfterPlayerTurnStart)),
            (typeof(InfernoPower), nameof(InfernoPower.AfterDamageReceived)),
            (typeof(OutbreakPower), nameof(OutbreakPower.AfterPowerAmountChanged)),
            (typeof(SmokestackPower), nameof(SmokestackPower.AfterCardGeneratedForCombat)),
        };
        var simplePrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.GenericPowerPrefix)));
        var simplePostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.GenericPowerPostfix)));
        foreach (var (t, m) in simpleMethods) _harmony.Patch(AccessTools.Method(t, m), prefix: simplePrefix, postfix: simplePostfix);

        // 2. Targeted FIFO Damage Trackers
        var targetedMethods = new (Type, string)[] {
            (typeof(StranglePower), nameof(StranglePower.AfterCardPlayed)),
            (typeof(DemisePower), nameof(DemisePower.AfterSideTurnEnd)),
        };
        var targetedPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TargetedPowerPrefix)));
        var targetedPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TargetedPowerPostfix)));
        foreach (var (t, m) in targetedMethods) _harmony.Patch(AccessTools.Method(t, m), prefix: targetedPrefix, postfix: targetedPostfix);

        // OblivionPower uses dedicated hooks to route its doom stacks into DoomHistory via CurrentOblivionContributions.
        _harmony.Patch(AccessTools.Method(typeof(OblivionPower), nameof(OblivionPower.AfterCardPlayed)),
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OblivionAfterCardPlayedPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OblivionAfterCardPlayedPostfix))));

        // 3. Middleman Buff Handoffs
        var handoffMethods = new (Type, string)[] {
            (typeof(DemonFormPower), nameof(DemonFormPower.AfterSideTurnStart)),
            (typeof(ArsenalPower), nameof(ArsenalPower.AfterCardGeneratedForCombat)),
            (typeof(ShadowStepPower), nameof(ShadowStepPower.AfterSideTurnStart)),
            (typeof(MonologuePower), nameof(MonologuePower.AfterCardPlayed)),
        };
        var handoffPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HandoffPowerPrefix)));
        var handoffPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HandoffPowerPostfix)));
        foreach (var (t, m) in handoffMethods) _harmony.Patch(AccessTools.Method(t, m), prefix: handoffPrefix, postfix: handoffPostfix);

        // 4. Proportional Share Trackers (poison-applying and Strength-handoff powers only)
        var propMethods = new (Type, string)[] {
            (typeof(NoxiousFumesPower), nameof(NoxiousFumesPower.AfterSideTurnStart)),
            (typeof(CorrosiveWavePower), nameof(CorrosiveWavePower.AfterCardDrawn)),
            (typeof(EnvenomPower), nameof(EnvenomPower.AfterDamageGiven)),
            (typeof(RupturePower), nameof(RupturePower.AfterDamageReceived)),
            (typeof(RupturePower), nameof(RupturePower.AfterCardPlayed)),
        };
        var propPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ProportionalPowerPrefix)));
        var propPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ProportionalPowerPostfix)));
        foreach (var (t, m) in propMethods) _harmony.Patch(AccessTools.Method(t, m), prefix: propPrefix, postfix: propPostfix);

        // 5. Queue Builders
        var queueMethods = new (Type, string)[] {
            (typeof(TrashToTreasurePower), nameof(TrashToTreasurePower.AfterCardGeneratedForCombat)),
            (typeof(LightningRodPower), nameof(LightningRodPower.AfterEnergyReset)),
            (typeof(SpinnerPower), nameof(SpinnerPower.AfterEnergyReset)),
        };
        var queuePrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.QueuePowerPrefix)));
        var queuePostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.QueuePowerPostfix)));
        foreach (var (t, m) in queueMethods) _harmony.Patch(AccessTools.Method(t, m), prefix: queuePrefix, postfix: queuePostfix);

        // --- ORB PATTERNS ---
        var orbMethods = new (Type powerType, string methodName, string prefixName, string postfixName)[] {
            (typeof(LightningOrb), nameof(LightningOrb.Passive), nameof(HookPatches.OrbPassivePrefix), nameof(HookPatches.OrbPassivePostfix)),
            (typeof(LightningOrb), nameof(LightningOrb.Evoke), nameof(HookPatches.OrbEvokePrefix), nameof(HookPatches.OrbEvokePostfix)),
            (typeof(GlassOrb), nameof(GlassOrb.Passive), nameof(HookPatches.OrbPassivePrefix), nameof(HookPatches.OrbPassivePostfix)),
            (typeof(GlassOrb), nameof(GlassOrb.Evoke), nameof(HookPatches.OrbEvokePrefix), nameof(HookPatches.OrbEvokePostfix)),
            (typeof(DarkOrb), nameof(DarkOrb.Passive), nameof(HookPatches.OrbPassivePrefix), nameof(HookPatches.OrbPassivePostfix)),
            (typeof(DarkOrb), nameof(DarkOrb.Evoke), nameof(HookPatches.OrbEvokePrefix), nameof(HookPatches.OrbEvokePostfix)),
        };
        foreach (var (powerType, methodName, prefixName, postfixName) in orbMethods) {
            _harmony.Patch(AccessTools.Method(powerType, methodName), 
                prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), prefixName)), 
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), postfixName)));
        }

        _harmony.Patch(AccessTools.Method(typeof(OrbCmd), nameof(OrbCmd.Channel), [typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player)]),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbChannelPostfix))));
            
        var tempApplyPrefix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix)));
        var tempApplyPostfix = new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix)));
        _harmony.Patch(AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.BeforeApplied)), prefix: tempApplyPrefix, postfix: tempApplyPostfix);
        _harmony.Patch(AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterPowerAmountChanged)), prefix: tempApplyPrefix, postfix: tempApplyPostfix);
        _harmony.Patch(AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterSideTurnEnd)), 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePostfix))));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        var original = AccessTools.Method(typeof(Hook), hookName);
        var postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}