using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Runs;
using Godot;

namespace DeckTracker;

[ModInitializer("Initialize")]
public static class ModEntry
{
    private static Harmony? _harmony;

    public static void Initialize()
    {
        if (_harmony != null) return;

        GD.Print("[DeckTracker] ModEntry.Initialize() called! Mod is loading.");

        _harmony = new Harmony("com.roni.sts2.deck_tracker");

        MethodInfo original = AccessTools.Method(typeof(Hook), nameof(Hook.BeforeCombatStart))
                              ?? throw new MissingMethodException(typeof(Hook).FullName, nameof(Hook.BeforeCombatStart));
        MethodInfo postfix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.BeforeCombatStartPostfix))
                             ?? throw new MissingMethodException(typeof(HookPatches).FullName, nameof(HookPatches.BeforeCombatStartPostfix));

        _harmony.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}

internal static class HookPatches
{
    private static bool _overlayScheduled;

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        GD.Print("[DeckTracker] Combat started. Spawning the UI.");
        
        // Spawn the overlay when combat starts
        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }
}