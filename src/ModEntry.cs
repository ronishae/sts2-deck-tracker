using System;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

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
        
        MethodInfo poisonOriginal = AccessTools.Method(typeof(PoisonPower), nameof(PoisonPower.AfterSideTurnStart));
        MethodInfo poisonPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPrefix));
        MethodInfo poisonPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.PoisonAfterSideTurnStartPostfix));
        
        _harmony!.Patch(poisonOriginal, 
            prefix: new HarmonyMethod(poisonPrefix), 
            postfix: new HarmonyMethod(poisonPostfix));
        
        MethodInfo fumesOriginal = AccessTools.Method(typeof(NoxiousFumesPower), nameof(NoxiousFumesPower.AfterSideTurnStart));
        MethodInfo fumesPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPrefix));
        MethodInfo fumesPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.FumesAfterSideTurnStartPostfix));
        
        _harmony!.Patch(fumesOriginal, 
            prefix: new HarmonyMethod(fumesPrefix), 
            postfix: new HarmonyMethod(fumesPostfix));
        
        MethodInfo waveOriginal = AccessTools.Method(typeof(CorrosiveWavePower), nameof(CorrosiveWavePower.AfterCardDrawn));
        MethodInfo wavePrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPrefix));
        MethodInfo wavePostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.WaveAfterCardDrawnPostfix));
        
        _harmony!.Patch(waveOriginal, 
            prefix: new HarmonyMethod(wavePrefix), 
            postfix: new HarmonyMethod(wavePostfix));
        
        PatchHook(nameof(Hook.AfterDiedToDoom), nameof(HookPatches.AfterDiedToDoomPostfix));
        
        MethodInfo doomKillOriginal = AccessTools.Method(typeof(DoomPower), nameof(DoomPower.DoomKill));
        MethodInfo doomKillPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.DoomKillPrefix));
        _harmony!.Patch(doomKillOriginal, prefix: new HarmonyMethod(doomKillPrefix));
        
        MethodInfo countdownOriginal = AccessTools.Method(typeof(CountdownPower), nameof(CountdownPower.AfterSideTurnStart));
        MethodInfo countdownPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPrefix));
        MethodInfo countdownPostfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.CountdownAfterSideTurnStartPostfix));
        
        _harmony!.Patch(countdownOriginal, 
            prefix: new HarmonyMethod(countdownPrefix), 
            postfix: new HarmonyMethod(countdownPostfix));
        
        // --- LIGHTNING ORB PATTERN ---
        MethodInfo lightningPassive = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Passive));
        _harmony!.Patch(lightningPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo lightningEvoke = AccessTools.Method(typeof(LightningOrb), nameof(LightningOrb.Evoke));
        _harmony!.Patch(lightningEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));

        // --- GLASS ORB PATTERN ---
        MethodInfo glassPassive = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Passive));
        _harmony!.Patch(glassPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo glassEvoke = AccessTools.Method(typeof(GlassOrb), nameof(GlassOrb.Evoke));
        _harmony!.Patch(glassEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        MethodInfo darkPassive = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Passive));
        _harmony!.Patch(darkPassive, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbPassivePostfix))));

        MethodInfo darkEvoke = AccessTools.Method(typeof(DarkOrb), nameof(DarkOrb.Evoke));
        _harmony!.Patch(darkEvoke, 
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePrefix))), 
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbEvokePostfix))));
        
        // Keep the Channel patch exactly as you have it, because OrbCmd is a static class!
        MethodInfo orbChannel = AccessTools.Method(typeof(MegaCrit.Sts2.Core.Commands.OrbCmd), nameof(MegaCrit.Sts2.Core.Commands.OrbCmd.Channel), new Type[] { typeof(MegaCrit.Sts2.Core.GameActions.Multiplayer.PlayerChoiceContext), typeof(OrbModel), typeof(MegaCrit.Sts2.Core.Entities.Players.Player) });
        _harmony!.Patch(orbChannel, postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.OrbChannelPostfix))));
        
        MethodInfo tempBefore = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.BeforeApplied));
        _harmony!.Patch(tempBefore, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        MethodInfo tempAfterAmount = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterPowerAmountChanged));
        _harmony!.Patch(tempAfterAmount, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusApplyPostfix))));

        MethodInfo tempEnd = AccessTools.Method(typeof(TemporaryFocusPower), nameof(TemporaryFocusPower.AfterTurnEnd));
        _harmony!.Patch(tempEnd, prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePrefix))), postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TempFocusExpirePostfix))));
        
        MethodInfo loopOriginal = AccessTools.Method(typeof(LoopPower), nameof(LoopPower.AfterPlayerTurnStart));
        _harmony!.Patch(loopOriginal,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(HookPatches), nameof(HookPatches.LoopPostfix))));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        MethodInfo original = AccessTools.Method(typeof(Hook), hookName);
        MethodInfo postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}