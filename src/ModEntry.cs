using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
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

        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterCombatEnd), nameof(HookPatches.AfterCombatEndPostfix)); 
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));

        MethodInfo playOriginal = AccessTools.Method(typeof(CardModel), nameof(CardModel.TryManualPlay))
            ?? throw new MissingMethodException("Could not find TryManualPlay");
        MethodInfo playPrefix = AccessTools.Method(typeof(HookPatches), nameof(HookPatches.TryManualPlayPrefix))
            ?? throw new MissingMethodException("Could not find TryManualPlayPrefix");
        _harmony.Patch(playOriginal, prefix: new HarmonyMethod(playPrefix));
    }

    private static void PatchHook(string hookName, string postfixName)
    {
        MethodInfo original = AccessTools.Method(typeof(Hook), hookName);
        MethodInfo postfix = AccessTools.Method(typeof(HookPatches), postfixName);
        _harmony!.Patch(original, postfix: new HarmonyMethod(postfix));
    }
}

internal static class HookPatches
{
    private static bool _overlayScheduled;

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        string seed = ExtractRunSeed(runState);
        CardRegistry.SyncRun(seed);

        // Figure out the room type BEFORE combat starts and kick off the real-time tracker
        string combatType = GetCombatType(runState);
        CardRegistry.StartCombat(combatType);

        ScanDeckForCards(runState);
        
        CardRegistry.ForcePublish();

        // REMOVED: CardRegistry.SaveState() -> Do not save mid-combat state to prevent Save & Quit bugs!

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState)
    {
        // Wrap up the combat and lock the data into the hard drive
        CardRegistry.ProcessCombatEnd();
    }

    // --- HELPERS & EXTRACTORS ---

    private static string GetCombatType(IRunState? runState)
    {
        if (runState != null)
        {
            try
            {
                var roomType = runState.BaseRoom?.RoomType;
                if (roomType == RoomType.Monster) return "Hallway";
                if (roomType == RoomType.Elite) return "Elite";
                if (roomType == RoomType.Boss) return "Boss";
            }
            catch { /* Fallback below */ }
        }
        return "Hallway";
    }

    private static string ExtractRunSeed(IRunState? runState)
    {
        if (runState == null) return "";
        try 
        {
            var rngProp = runState.GetType().GetProperty("Rng");
            var rng = rngProp?.GetValue(runState);
            var seedProp = rng?.GetType().GetProperty("StringSeed");
            return seedProp?.GetValue(rng)?.ToString() ?? "";
        } 
        catch { return ""; }
    }

    private static void ScanDeckForCards(IRunState? runState)
    {
        if (runState == null) return;
        try 
        {
            var players = GetEnumerableProperty(runState, "Players");
            if (players == null) return;

            foreach (var player in players) ScanPlayerPiles(player);
        } 
        catch { }
    }

    private static void ScanPlayerPiles(object player)
    {
        var piles = GetEnumerableProperty(player, "Piles");
        if (piles == null) return;

        foreach (var pile in piles) 
        {
            var cards = GetEnumerableProperty(pile, "Cards");
            if (cards == null) continue; 

            foreach (var card in cards) 
            {
                if (card is CardModel cardModel) CardRegistry.RegisterCard(cardModel);
            }
        }
    }

    private static System.Collections.IEnumerable? GetEnumerableProperty(object obj, string propertyName)
    {
        return obj.GetType().GetProperty(propertyName)?.GetValue(obj) as System.Collections.IEnumerable;
    }

    // --- DAMAGE HOOKS ---
    public static void TryManualPlayPrefix(CardModel __instance)
    {
        CardRegistry.RegisterCard(__instance);
        CardRegistry.ForcePublish();
    }

    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, CombatState? combatState, Creature? dealer, DamageResult? results, ValueProp props, Creature? target, CardModel? cardSource)
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