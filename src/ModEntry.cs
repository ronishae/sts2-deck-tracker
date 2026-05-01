using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
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

        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        MethodInfo original = AccessTools.Method(typeof(Hook), hookName)
            ?? throw new MissingMethodException(typeof(Hook).FullName, hookName);
        MethodInfo postfix = AccessTools.Method(typeof(HookPatches), postfixName)
            ?? throw new MissingMethodException(typeof(HookPatches).FullName, postfixName);

        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}

internal static class HookPatches
{
    private static bool _overlayScheduled;
    private static IRunState? _lastRunState;

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        // If we detect a completely new run, wipe the run stats
        if (runState != null && runState != _lastRunState)
        {
            _lastRunState = runState;
            DeckDamageService.ResetRun();
        }

        // Always reset combat stats at the start of a fight
        DeckDamageService.ResetCombat();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterDamageGivenPostfix(
        PlayerChoiceContext? choiceContext,
        CombatState? combatState,
        Creature? dealer,
        DamageResult? results,
        ValueProp props,
        Creature? target,
        CardModel? cardSource)
    {
        if (dealer != null && (dealer.IsPlayer || dealer.Side == CombatSide.Player))
        {
            if (results != null && cardSource != null && results.UnblockedDamage > 0)
            {
                DeckDamageService.RecordDamage(cardSource, results.UnblockedDamage);
            }
        }
    }
}