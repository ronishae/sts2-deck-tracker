using Godot;
using HarmonyLib;
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

        var serpentOriginal = AccessTools.Method(typeof(SerpentFormPower), nameof(SerpentFormPower.AfterCardPlayed));
        var serpentPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SerpentFormAfterCardPlayedPrefix));
        var serpentPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.SerpentFormAfterCardPlayedPostfix));
        
        _harmony.Patch(serpentOriginal, 
            prefix: new HarmonyMethod(serpentPrefix), 
            postfix: new HarmonyMethod(serpentPostfix));
        
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

        var tempEnd = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterTurnEnd));
        _harmony.Patch(tempEnd, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePostfix))));
        
        var loopOriginal = AccessTools.Method(typeof(LoopPower), nameof(LoopPower.AfterPlayerTurnStart));
        _harmony.Patch(loopOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPostfix))));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        var original = AccessTools.Method(typeof(Hook), hookName);
        var postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}