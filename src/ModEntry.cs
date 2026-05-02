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

        // 1. Hook Game Events
        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));

        // 2. Hook Card Play (Catches 0 damage cards and mid-combat generated cards)
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
    private static IRunState? _lastRunState;

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        if (runState != null && runState != _lastRunState)
        {
            _lastRunState = runState;
            DeckDamageService.ResetRun();
        }

        DeckDamageService.ResetCombat();

        // --- THE DECK SCANNER ---
        // Uses safe reflection to dig into the player's piles and register every card instantly
        try 
        {
            if (runState != null) 
            {
                var playersProp = runState.GetType().GetProperty("Players");
                if (playersProp?.GetValue(runState) is System.Collections.IEnumerable players) 
                {
                    foreach (var player in players) 
                    {
                        var pilesProp = player.GetType().GetProperty("Piles");
                        if (pilesProp?.GetValue(player) is System.Collections.IEnumerable piles) 
                        {
                            foreach (var pile in piles) 
                            {
                                var cardsProp = pile.GetType().GetProperty("Cards");
                                if (cardsProp?.GetValue(pile) is System.Collections.IEnumerable cards) 
                                {
                                    foreach (var card in cards) 
                                    {
                                        if (card is CardModel cardModel) 
                                        {
                                            DeckDamageService.RegisterCard(cardModel);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        } 
        catch { /* Fails silently if STS2 changes their API */ }
        // ------------------------

        // Push the freshly scanned deck to the UI
        DeckDamageService.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    // Catches ALL cards as you play them, ensuring Defends/Skills get onto the list!
    public static void TryManualPlayPrefix(CardModel __instance)
    {
        DeckDamageService.RegisterCard(__instance);
        DeckDamageService.ForcePublish();
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