using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
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

        // --- Core Lifecycle Hooks ---
        PatchHook(nameof(Hook.BeforeCombatStart), nameof(HookPatches.BeforeCombatStartPostfix));
        PatchHook(nameof(Hook.AfterCombatEnd), nameof(HookPatches.AfterCombatEndPostfix)); 
        
        // --- Damage Hooks ---
        PatchHook(nameof(Hook.AfterDamageGiven), nameof(HookPatches.AfterDamageGivenPostfix));

        // --- Card Event Hooks ---
        PatchHook(nameof(Hook.AfterCardDrawn), nameof(HookPatches.AfterCardDrawnPostfix));
        PatchHook(nameof(Hook.AfterCardChangedPiles), nameof(HookPatches.AfterCardChangedPilesPostfix));
        PatchHook(nameof(Hook.BeforeCardPlayed), nameof(HookPatches.BeforeCardPlayedPostfix));
        PatchHook(nameof(Hook.BeforeCardAutoPlayed), nameof(HookPatches.BeforeCardAutoPlayedPostfix));
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
    
    // Catches cards that enter the hand via Generation, Exhaust, or Discard retrieval!
    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? source)
    {
        // We only care if we are actively in combat
        if (combatState == null) return;

        try
        {
            // If the card is now in the Hand, but it didn't come from the Draw pile 
            // (because our other AfterCardDrawn hook already handles standard draws)
            if (card.Pile != null && card.Pile.Type == PileType.Hand && oldPile != PileType.Draw)
            {
                CardRegistry.RegisterCard(card);
                CardRegistry.AddDraw(card);
                CardRegistry.ForcePublish();
            }
        }
        catch { /* Fails silently if Pile data is missing */ }
    }
    
    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        string seed = ExtractRunSeed(runState);
        CardRegistry.SyncRun(seed);

        string combatType = GetCombatType(runState);
        CardRegistry.StartCombat(combatType);

        ScanDeckForCards(runState);
        CardRegistry.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState)
    {
        CardRegistry.ProcessCombatEnd();
    }

    // --- NEW: CLEAN EVENT HOOKS ---

    // Catches ALL cards entering your hand
    public static void AfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        CardRegistry.RegisterCard(card);
        CardRegistry.AddDraw(card);
    }

    // Catches manual plays (spending energy)
    public static void BeforeCardPlayedPostfix(ICombatState combatState, CardPlay cardPlay)
    {
        try
        {
            // We use reflection here just in case CardPlay changes in future Early Access builds,
            // but it reliably holds a reference to the CardModel being played.
            var cardProp = cardPlay.GetType().GetProperty("Card");
            if (cardProp?.GetValue(cardPlay) is CardModel card)
            {
                CardRegistry.RegisterCard(card);
                CardRegistry.AddPlay(card);
                CardRegistry.ForcePublish();
            }
        }
        catch { /* Silently fail if STS2 changes the CardPlay object */ }
    }

    // Catches automatic plays (Echo Form, Mayhem, Havoc, etc.)
    public static void BeforeCardAutoPlayedPostfix(ICombatState combatState, CardModel card, Creature? target, AutoPlayType type)
    {
        CardRegistry.RegisterCard(card);
        CardRegistry.AddPlay(card);
        CardRegistry.ForcePublish();
    }

    // Catches all damage dealt
    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (dealer != null && (dealer.IsPlayer || dealer.Side == CombatSide.Player))
        {
            if (cardSource != null && results.UnblockedDamage > 0)
            {
                DeckDamageService.RecordDamage(cardSource, results.UnblockedDamage);
            }
        }
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
            catch { }
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
}