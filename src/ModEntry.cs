using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

[ModInitializer("Initialize")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null) return;
        _harmony = new Harmony("com.yourname.sts2.deck_tracker");

        // --- Core Lifecycle Hooks ---
        PatchHook(nameof(Hook.AfterRoomEntered), nameof(HookPatches.AfterRoomEnteredPostfix));
        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterCombatEnd), nameof(HookPatches.AfterCombatEndPostfix)); 
        
        // --- Damage Hooks ---
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));
        PatchHook(nameof(Hook.AfterForge), nameof(HookPatches.AfterForgePostfix));
        
        // --- Card Removal Hook ---
        PatchHook(nameof(Hook.BeforeCardRemoved), nameof(HookPatches.BeforeCardRemovedPostfix));
        
        // --- Card Event Hooks ---
        PatchHook(nameof(Hook.AfterCardDrawn), nameof(HookPatches.AfterCardDrawnPostfix));
        PatchHook(nameof(Hook.AfterCardChangedPiles), nameof(HookPatches.AfterCardChangedPilesPostfix));
        PatchHook(nameof(Hook.BeforeCardPlayed), nameof(HookPatches.BeforeCardPlayedPostfix));
        PatchHook(nameof(Hook.AfterCardPlayed), nameof(HookPatches.AfterCardPlayedPostfix));
        PatchHook(nameof(Hook.BeforePowerAmountChanged), nameof(HookPatches.BeforePowerAmountChangedPostfix));
        
        var poisonOriginal = AccessTools.Method(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart));
        var poisonPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPrefix));
        var poisonPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPostfix));
        
        _harmony.Patch(poisonOriginal, 
            prefix: new HarmonyMethod(poisonPrefix), 
            postfix: new HarmonyMethod(poisonPostfix));
        
        var fumesOriginal = AccessTools.Method(typeof(NoxiousFumesPower), nameof(NoxiousFumesPower.AfterSideTurnStart));
        var fumesPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPrefix));
        var fumesPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPostfix));
        
        _harmony.Patch(fumesOriginal, 
            prefix: new HarmonyMethod(fumesPrefix), 
            postfix: new HarmonyMethod(fumesPostfix));
        
        var waveOriginal = AccessTools.Method(typeof(CorrosiveWavePower), nameof(CorrosiveWavePower.AfterCardDrawn));
        var wavePrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPrefix));
        var wavePostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPostfix));
        
        _harmony.Patch(waveOriginal, 
            prefix: new HarmonyMethod(wavePrefix), 
            postfix: new HarmonyMethod(wavePostfix));
        
        PatchHook(nameof(Hook.AfterDiedToDoom), nameof(HookPatches.AfterDiedToDoomPostfix));
        
        var doomKillOriginal = AccessTools.Method(typeof(DoomPower), nameof(DoomPower.DoomKill));
        var doomKillPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DoomKillPrefix));
        _harmony.Patch(doomKillOriginal, prefix: new HarmonyMethod(doomKillPrefix));
        
        var countdownOriginal = AccessTools.Method(typeof(CountdownPower), nameof(CountdownPower.AfterSideTurnStart));
        var countdownPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPrefix));
        var countdownPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPostfix));
        
        _harmony.Patch(countdownOriginal, 
            prefix: new HarmonyMethod(countdownPrefix), 
            postfix: new HarmonyMethod(countdownPostfix));

        var strangleOriginal = AccessTools.Method(typeof(StranglePower), nameof(StranglePower.AfterCardPlayed));
        var stranglePrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.StrangleAfterCardPlayedPrefix));
        var stranglePostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.StrangleAfterCardPlayedPostfix));
        
        _harmony.Patch(strangleOriginal, 
            prefix: new HarmonyMethod(stranglePrefix), 
            postfix: new HarmonyMethod(stranglePostfix));

        var oblivionOriginal = AccessTools.Method(typeof(OblivionPower), nameof(OblivionPower.AfterCardPlayed));
        var oblivionPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OblivionAfterCardPlayedPrefix));
        var oblivionPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OblivionAfterCardPlayedPostfix));

        _harmony.Patch(oblivionOriginal,
            prefix: new HarmonyMethod(oblivionPrefix),
            postfix: new HarmonyMethod(oblivionPostfix));

        var serpentOriginal = AccessTools.Method(typeof(SerpentFormPower), nameof(SerpentFormPower.AfterCardPlayed));
        var serpentPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SerpentFormAfterCardPlayedPrefix));
        var serpentPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SerpentFormAfterCardPlayedPostfix));
        
        _harmony.Patch(serpentOriginal, 
            prefix: new HarmonyMethod(serpentPrefix), 
            postfix: new HarmonyMethod(serpentPostfix));

        var reaperOriginal = AccessTools.Method(typeof(ReaperFormPower), nameof(ReaperFormPower.AfterDamageGiven));
        var reaperPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReaperFormAfterDamageGivenPrefix));
        var reaperPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReaperFormAfterDamageGivenPostfix));

        _harmony.Patch(reaperOriginal,
            prefix: new HarmonyMethod(reaperPrefix),
            postfix: new HarmonyMethod(reaperPostfix));

        var blackHolePlayedOriginal = AccessTools.Method(typeof(BlackHolePower), nameof(BlackHolePower.AfterCardPlayed));
        var blackHoleStarsOriginal = AccessTools.Method(typeof(BlackHolePower), nameof(BlackHolePower.AfterStarsGained));
        
        _harmony.Patch(blackHolePlayedOriginal, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BlackHoleAfterCardPlayedPrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BlackHoleAfterCardPlayedPostfix))));

        _harmony.Patch(blackHoleStarsOriginal, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BlackHoleAfterStarsGainedPrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BlackHoleAfterStarsGainedPostfix))));

        var sleightOriginal = AccessTools.Method(typeof(SleightOfFleshPower), nameof(SleightOfFleshPower.AfterPowerAmountChanged));
        var sleightPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SleightOfFleshAfterPowerAmountChangedPrefix));
        var sleightPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SleightOfFleshAfterPowerAmountChangedPostfix));
        
        _harmony.Patch(sleightOriginal, 
            prefix: new HarmonyMethod(sleightPrefix), 
            postfix: new HarmonyMethod(sleightPostfix));

        var hauntOriginal = AccessTools.Method(typeof(HauntPower), nameof(HauntPower.AfterCardPlayed));
        var hauntPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HauntAfterCardPlayedPrefix));
        var hauntPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HauntAfterCardPlayedPostfix));
        
        _harmony.Patch(hauntOriginal, 
            prefix: new HarmonyMethod(hauntPrefix), 
            postfix: new HarmonyMethod(hauntPostfix));

        var juggernautOriginal = AccessTools.Method(typeof(JuggernautPower), nameof(JuggernautPower.AfterBlockGained));
        var juggernautPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.JuggernautAfterBlockGainedPrefix));
        var juggernautPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.JuggernautAfterBlockGainedPostfix));
        
        _harmony.Patch(juggernautOriginal, 
            prefix: new HarmonyMethod(juggernautPrefix), 
            postfix: new HarmonyMethod(juggernautPostfix));

        var necroMasteryOriginal = AccessTools.Method(typeof(NecroMasteryPower), nameof(NecroMasteryPower.AfterCurrentHpChanged));
        var necroMasteryPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.NecroMasteryAfterCurrentHpChangedPrefix));
        var necroMasteryPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.NecroMasteryAfterCurrentHpChangedPostfix));

        _harmony.Patch(necroMasteryOriginal,
            prefix: new HarmonyMethod(necroMasteryPrefix),
            postfix: new HarmonyMethod(necroMasteryPostfix));

        var thornsOriginal = AccessTools.Method(typeof(ThornsPower), nameof(ThornsPower.BeforeDamageReceived));
        var thornsPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ThornsBeforeDamageReceivedPrefix));
        var thornsPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ThornsBeforeDamageReceivedPostfix));

        _harmony.Patch(thornsOriginal,
            prefix: new HarmonyMethod(thornsPrefix),
            postfix: new HarmonyMethod(thornsPostfix));

        var flameBarrierOriginal = AccessTools.Method(typeof(FlameBarrierPower), nameof(FlameBarrierPower.AfterDamageReceived));
        var flameBarrierPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FlameBarrierAfterDamageReceivedPrefix));
        var flameBarrierPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FlameBarrierAfterDamageReceivedPostfix));
        
        _harmony.Patch(flameBarrierOriginal, 
            prefix: new HarmonyMethod(flameBarrierPrefix), 
            postfix: new HarmonyMethod(flameBarrierPostfix));

        var reflectOriginal = AccessTools.Method(typeof(ReflectPower), nameof(ReflectPower.AfterDamageReceived));
        var reflectPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReflectAfterDamageReceivedPrefix));
        var reflectPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ReflectAfterDamageReceivedPostfix));
        
        _harmony.Patch(reflectOriginal, 
            prefix: new HarmonyMethod(reflectPrefix), 
            postfix: new HarmonyMethod(reflectPostfix));

        var speedsterOriginal = AccessTools.Method(typeof(SpeedsterPower), nameof(SpeedsterPower.AfterCardDrawn));
        var speedsterPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SpeedsterAfterCardDrawnPrefix));
        var speedsterPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SpeedsterAfterCardDrawnPostfix));

        _harmony.Patch(speedsterOriginal, 
            prefix: new HarmonyMethod(speedsterPrefix), 
            postfix: new HarmonyMethod(speedsterPostfix));

        var thunderOriginal = AccessTools.Method(typeof(ThunderPower), nameof(ThunderPower.AfterOrbEvoked));
        var thunderPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ThunderAfterOrbEvokedPrefix));
        var thunderPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ThunderAfterOrbEvokedPostfix));

        _harmony.Patch(thunderOriginal,
            prefix: new HarmonyMethod(thunderPrefix),
            postfix: new HarmonyMethod(thunderPostfix));

        var stormOriginal = AccessTools.Method(typeof(StormPower), nameof(StormPower.AfterCardPlayed));
        var stormPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.StormAfterCardPlayedPrefix));
        var stormPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.StormAfterCardPlayedPostfix));

        _harmony.Patch(stormOriginal,
            prefix: new HarmonyMethod(stormPrefix),
            postfix: new HarmonyMethod(stormPostfix));

        var hailstormOriginal = AccessTools.Method(typeof(HailstormPower), nameof(HailstormPower.BeforeSideTurnEnd));
        var hailstormPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HailstormBeforeTurnEndPrefix));
        var hailstormPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.HailstormBeforeTurnEndPostfix));

        _harmony.Patch(hailstormOriginal,
            prefix: new HarmonyMethod(hailstormPrefix),
            postfix: new HarmonyMethod(hailstormPostfix));

        var powerRemoveOriginal = AccessTools.Method(typeof(PowerCmd), nameof(PowerCmd.Remove), new[] { typeof(PowerModel) });        var powerRemovePrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BeforePowerRemovedPrefix));
        _harmony.Patch(powerRemoveOriginal, prefix: new HarmonyMethod(powerRemovePrefix));
        
        // --- LIGHTNING ORB PATTERN ---
        var lightningPassive = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Passive));
        _harmony.Patch(lightningPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        var lightningEvoke = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Evoke));
        _harmony.Patch(lightningEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));

        // --- GLASS ORB PATTERN ---
        var glassPassive = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Passive));
        _harmony.Patch(glassPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        var glassEvoke = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Evoke));
        _harmony.Patch(glassEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        var darkPassive = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Passive));
        _harmony.Patch(darkPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        var darkEvoke = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Evoke));
        _harmony.Patch(darkEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        var orbChannel = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.OrbCmd), nameof(MegaCrit.Sts2.Core.Commands.OrbCmd.Channel),
            [typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(OrbModel), typeof(MegaCrit.Sts2.Core.Entities.Players.Player)
            ]);
        _harmony.Patch(orbChannel, postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbChannelPostfix))));
        
        var tempBefore = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.BeforeApplied));
        _harmony.Patch(tempBefore, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        var tempAfterAmount = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterPowerAmountChanged));
        _harmony.Patch(tempAfterAmount, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        var tempEnd = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterSideTurnEnd));
        _harmony.Patch(tempEnd, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePostfix))));
        
        var loopOriginal = AccessTools.Method(typeof(LoopPower), nameof(LoopPower.AfterPlayerTurnStart));
        _harmony.Patch(loopOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPostfix))));

        var boulderOriginal = AccessTools.Method(typeof(RollingBoulderPower), nameof(RollingBoulderPower.AfterPlayerTurnStart));
        var boulderPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RollingBoulderAfterPlayerTurnStartPrefix));
        var boulderPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.RollingBoulderAfterPlayerTurnStartPostfix));

        _harmony.Patch(boulderOriginal,
            prefix: new HarmonyMethod(boulderPrefix),
            postfix: new HarmonyMethod(boulderPostfix));
        
        // --- PREP TIME POWER ---
        var prepTimeOriginal = AccessTools.Method(typeof(PrepTimePower), nameof(PrepTimePower.AfterSideTurnStart));
        _harmony.Patch(prepTimeOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PrepTimePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PrepTimePostfix))));
        
        // --- ADDITIVE/MULTIPLICATIVE DAMAGE CALCULATOR ---
        var modifyDamage = AccessTools.Method(typeof(Hook), nameof(Hook.ModifyDamage));
        _harmony.Patch(modifyDamage, 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ModifyDamagePostfix))));
        
        // --- SHADOW STEP POWER ---
        var shadowStepOriginal = AccessTools.Method(typeof(ShadowStepPower), nameof(ShadowStepPower.AfterSideTurnStart));
        _harmony.Patch(shadowStepOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ShadowStepPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ShadowStepPostfix))));
        
        var demonFormOriginal = AccessTools.Method(typeof(DemonFormPower), nameof(DemonFormPower.AfterSideTurnStart));
        _harmony.Patch(demonFormOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DemonFormPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DemonFormPostfix))));
        
        var arsenalOriginal = AccessTools.Method(typeof(ArsenalPower), nameof(ArsenalPower.AfterCardGeneratedForCombat));
        _harmony.Patch(arsenalOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ArsenalPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.ArsenalPostfix))));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        var original = AccessTools.Method(typeof(Hook), hookName);
        var postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}