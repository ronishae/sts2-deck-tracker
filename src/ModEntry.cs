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
    
    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room)
    {
        int currentFloor = ExtractFloorNum(runState);
        List<string> activeDeckIds = ScanDeckForCards(runState);
        
        // Sync the deck the moment we step into a new room to catch Upgrades/Transforms immediately
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
    }
    
    public static void BeforeCardRemovedPostfix(IRunState runState, CardModel card)
    {
        int currentFloor = ExtractFloorNum(runState);
        CardRegistry.HandleRemove(card, currentFloor);
    }
    
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
    
        int currentFloor = ExtractFloorNum(runState);
        string combatType = GetCombatType(runState);
        
        // 1. Scan deck first to register cards and get the active list
        List<string> activeDeckIds = ScanDeckForCards(runState);
        
        // 2. Start combat and pass the active list so it can diff against the tracker history
        CardRegistry.StartCombat(combatType, currentFloor, activeDeckIds);
        
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
                // Do not count Replay in the times play tracker
                if (cardPlay.PlayIndex == 0)
                {
                    CardRegistry.AddPlay(card);
                }
                CardRegistry.ForcePublish();
            }
        }
        catch { /* Silently fail if STS2 changes the CardPlay object */ }
    }
    
    public static void BeforePowerAmountChangedPostfix(
        ICombatState combatState,
        PowerModel power,
        Decimal amount,
        Creature target,
        Creature? applier,
        CardModel? cardSource)
    {
        GD.Print($"[DeckTracker] Card {cardSource?.Id.Entry} did {power} power with amount {amount} {target.Name}.");
        switch (power)
        {
            case ConquerorPower:
                CardRegistry.UpdateConquerorTracker(target, amount, cardSource);
                break;
            case SwordSagePower:
            {
                CardRegistry.UpdateSovereignBladeReplayModifierTracker(amount, cardSource);
                break;
            }
            case FurnacePower:
                CardRegistry.UpdateFurnaceHistory(amount, cardSource);
                break;
        }
    }
    
    public static void AfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        string cardId = cardPlay.Card.Id.Entry ?? "";
        GD.Print($"[DeckTracker] Card {cardId} played with PlayCount: {cardPlay.PlayCount} and PlayIndex: {cardPlay.PlayIndex}.");

        if (cardId.Equals("SEEKING_EDGE")) 
        {
            CardRegistry.UpdateSeekingEdge(cardPlay.Card);
        }
        else if (cardId.Equals("SOVEREIGN_BLADE"))
        {
            CardRegistry.ProcessSovereignBladeHistory(cardPlay);
        }
    }
    
    public static void AfterForgePostfix(ICombatState combatState, decimal amount, Player forger, AbstractModel? source)
    {
        GD.Print($"[DeckTracker] Card {source?.Id.Entry} did {amount} forge.");
        if (source is CardModel card)
        {
            CardRegistry.AddForge(card, amount);
        }
        else if (source is PowerModel power)
        {
            if (power is FurnacePower)
            {
                CardRegistry.HandleFurnaceForge(amount);
            }
        }
    }
    
    // Catches all damage dealt
    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (cardSource == null) return;
        
        // If the card is Sovereign Blade, process the forge distribution!
        GD.Print($"[DeckTracker] Card {cardSource.Id.Entry} did {results.TotalDamage} damage to {target.Name} with target type {cardSource.TargetType}");
        GD.Print($"[DeckTracker] {combatState.Enemies.Count} enemies in the combat via after damage");
        if (cardSource.Id.Entry.Equals("SOVEREIGN_BLADE")) 
        {
            var damageHistoryItem = new DamageHistoryItem(combatState, dealer, results, target, cardSource);
            CardRegistry.AddSovereignBladeDamageHistoryItem(damageHistoryItem);
        }
        else
        {
            CardRegistry.AddDamage(cardSource, results.TotalDamage); 
        }
    }

    // --- HELPERS & EXTRACTORS ---
    private static int ExtractFloorNum(IRunState? runState)
    {
        if (runState == null) return 1;
        return runState.TotalFloor;
    }
    
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

    private static List<string> ScanDeckForCards(IRunState? runState)
    {
        List<string> deckIds = new();
        
        if (runState == null) return deckIds;
        try 
        {
            var players = runState.Players;
            if (players.Count == 0) return deckIds;

            ScanPlayerPiles(players[0], deckIds);
        } 
        catch { }
        return deckIds;
    }

    private static void ScanPlayerPiles(Player player, List<string> deckIds)
    {
        var deck = player.Deck;

        foreach (var card in deck.Cards) 
        {
            CardRegistry.RegisterCard(card);
            deckIds.Add(CardRegistry.GetTrackingId(card));
        }
    }

    private static System.Collections.IEnumerable? GetEnumerableProperty(object obj, string propertyName)
    {
        return obj.GetType().GetProperty(propertyName)?.GetValue(obj) as System.Collections.IEnumerable;
    }
}