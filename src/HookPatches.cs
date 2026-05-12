using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace DeckTracker;

internal static class HookPatches
{
    private static bool _overlayScheduled;
    
    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room)
    {
        var currentFloor = ExtractFloorNum(runState);
        var activeDeckIds = ScanDeckForCards(runState);
        
        // Sync the deck the moment we step into a new room to catch Upgrades/Transforms immediately
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
    }
    
    public static void BeforeCardRemovedPostfix(IRunState runState, CardModel card)
    {
        var currentFloor = ExtractFloorNum(runState);
        CardRegistry.HandleRemove(card, currentFloor);
    }
    
    // Catches cards that enter the hand via Generation, Exhaust, or Discard retrieval!
    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? source)
    {
        // We only care if we are actively in combat
        if (combatState == null) return;
        GD.Print($"[DeckTracker] AfterCardChangedPiles: pile: {oldPile} - card: {card.Id.Entry} - source: {source?.Id.Entry}.");
        try
        {
            // If the card is now in the Hand, but it didn't come from the Draw pile 
            // (because our other AfterCardDrawn hook already handles standard draws)
            if (card.Pile != null && card.Pile.Type == PileType.Hand && oldPile != PileType.Draw)
            {
                if (CardRegistry.IsCardPlayActive())
                {
                    CardRegistry.DeferDraw(card);
                    GD.Print($"[DeckTracker] AfterCardChangedPiles: Deferring draw for {card.Id.Entry} until play finishes.");
                }
                else
                {
                    CardRegistry.RegisterCard(card);
                    CardRegistry.AddDraw(card);
                    CardRegistry.ForcePublish();
                }
            }
        }
        catch { /* Fails silently if Pile data is missing */ }
    }
    
    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        var seed = ExtractRunSeed(runState);
        CardRegistry.SyncRun(seed);
    
        var currentFloor = ExtractFloorNum(runState);
        var currentAct = ExtractActNum(runState);
        var combatType = GetCombatType(runState);
        
        // 1. Scan deck first to register cards and get the active list
        var activeDeckIds = ScanDeckForCards(runState);
        
        // 2. Start combat and pass the active list so it can diff against the tracker history
        CardRegistry.StartCombat(combatType, currentFloor, currentAct, activeDeckIds);
        
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
                CardRegistry.StartCardPlay(card);
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
            case NoxiousFumesPower:
                if (amount > 0) CardRegistry.AddFumesShares(amount, cardSource);
                break;
            case StranglePower:
                if (amount > 0) CardRegistry.LogStrangleApply(target, cardSource, (int)amount);
                else if (amount < 0) CardRegistry.ClearStrangle(target);
                break;
            case SerpentFormPower:
                if (amount > 0) CardRegistry.LogSerpentFormApply(cardSource, (int)amount);
                break;
            case BlackHolePower:
                if (amount > 0) CardRegistry.LogBlackHoleApply(cardSource, (int)amount);
                break;
            case SleightOfFleshPower:
                if (amount > 0) CardRegistry.LogSleightOfFleshApply(cardSource, (int)amount);
                break;
            case CorrosiveWavePower:
                if (amount > 0) 
                {
                    CardRegistry.AddCorrosiveWaveShares(amount, cardSource);
                }
                else if (amount < 0) 
                {
                    // The power is removed at end of turn. Just dump the bucket!
                    CardRegistry.ClearCorrosiveWaveShares();
                }
                break;
            case PoisonPower:
                if (amount > 0)
                {
                    // Is Fumes currently the thing applying this Poison?
                    if (CardRegistry.IsNoxiousFumesExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalFumes = CardRegistry.FumesShares.Sum(x => x.Shares);
                            if (totalFumes > 0)
                            {
                                // Distribute the new Poison proportionally based on who applied the Fumes
                                foreach (var share in CardRegistry.FumesShares)
                                {
                                    decimal proportion = share.Shares / totalFumes;
                                    CardRegistry.AddPoisonSharesById(target, amount * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    else if (CardRegistry.IsCorrosiveWaveExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalWave = CardRegistry.CorrosiveWaveShares.Sum(x => x.Shares);
                            if (totalWave > 0)
                            {
                                foreach (var share in CardRegistry.CorrosiveWaveShares)
                                {
                                    decimal proportion = share.Shares / totalWave;
                                    CardRegistry.AddPoisonSharesById(target, amount * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    else
                    {
                        GD.Print($"[DeckTracker] Adding poison power {amount} from card {cardSource?.Id.Entry}.");
                        CardRegistry.AddPoisonShares(target, amount, cardSource);
                    }
                }
                else if (amount < 0)
                {
                    // Amount is negative (e.g., -1), so we use Math.Abs to pass a positive 1 to our math function.
                    CardRegistry.RemovePoisonSharesProportionally(target, Math.Abs(amount));
                }
                break;
            case CountdownPower:
                if (amount > 0) CardRegistry.AddCountdownHistory(amount, cardSource);
                break;
            case DoomPower:
                if (amount > 0)
                {
                    // Is Countdown currently applying this Doom?
                    if (CardRegistry.IsCountdownExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal remainingDoom = amount;

                            // Read the Countdown history top-to-bottom and assign the Doom
                            foreach (var contribution in CardRegistry.CountdownHistory)
                            {
                                if (remainingDoom <= 0) break;

                                decimal amountToAttribute = Math.Min(remainingDoom, contribution.Amount);
                                CardRegistry.AddDoomHistoryById(target, amountToAttribute, contribution.TrackingId);

                                remainingDoom -= amountToAttribute;
                            }
                        }
                    }
                    else {
                        CardRegistry.AddDoomHistory(target, amount, cardSource);
                    }
                }
                break;
            case FocusPower:
                CardRegistry.LogFocusChange(cardSource, amount);
                break;
            case LoopPower:
                if (amount > 0) CardRegistry.AddLoop(amount, cardSource);
                break;
        }
    }
    
    public static void AfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        CardRegistry.EndCardPlay();
        CardRegistry.ForcePublish();
        
        var cardId = cardPlay.Card.Id.Entry ?? "";
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
    
    // The parameters MUST be named this way, with double underscores or Harmony will have errors. Do not change.
    public static void PoisonAfterSideTurnStartPrefix(PoisonPower __instance)
    {
        if (!__instance.Owner.IsPlayer)
        {
            GD.Print($"[DeckTracker] Setting Poison context for {__instance.Owner.Name} in PREFIX");
            CardRegistry.CurrentPoisonTarget.Value = __instance.Owner;
        }
    }
    
    public static void PoisonAfterSideTurnStartPostfix(PoisonPower __instance, ref Task __result)
    {
        if (!__instance.Owner.IsPlayer)
        {
            __result = CardRegistry.AwaitPoisonTaskAsync(__result);
        }
    }
    
    public static void FumesAfterSideTurnStartPrefix(NoxiousFumesPower __instance)
    {
        CardRegistry.IsNoxiousFumesExecuting.Value = true;
    }

    public static void FumesAfterSideTurnStartPostfix(NoxiousFumesPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitFumesTaskAsync(__result);
    }
    
    public static void WaveAfterCardDrawnPrefix(CorrosiveWavePower __instance)
    {
        CardRegistry.IsCorrosiveWaveExecuting.Value = true;
    }

    public static void WaveAfterCardDrawnPostfix(CorrosiveWavePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitCorrosiveWaveTaskAsync(__result);
    }
    
    public static void DoomKillPrefix(IReadOnlyList<Creature> creatures)
    {
        CardRegistry.CapturePendingDoomHp(creatures);
    }

    public static void AfterDiedToDoomPostfix(ICombatState combatState, IReadOnlyList<Creature> creatures)
    {
        CardRegistry.DistributeDoomDamage(creatures);
    }
    
    public static void CountdownAfterSideTurnStartPrefix(CountdownPower __instance)
    {
        CardRegistry.IsCountdownExecuting.Value = true;
    }

    public static void CountdownAfterSideTurnStartPostfix(CountdownPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitCountdownTaskAsync(__result);
    }

    public static void StrangleAfterCardPlayedPrefix(StranglePower __instance)
    {
        CardRegistry.IsStrangleExecuting.Value = true;
    }

    public static void StrangleAfterCardPlayedPostfix(StranglePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitStrangleTaskAsync(__result, __instance.Owner, (decimal)__instance.Amount);
    }

    public static void SerpentFormAfterCardPlayedPrefix(SerpentFormPower __instance)
    {
        CardRegistry.IsSerpentFormExecuting.Value = true;
    }

    public static void SerpentFormAfterCardPlayedPostfix(SerpentFormPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitSerpentFormTaskAsync(__result);
    }

    public static void BlackHoleAfterCardPlayedPrefix(BlackHolePower __instance)
    {
        CardRegistry.IsBlackHoleExecuting.Value = true;
    }

    public static void BlackHoleAfterCardPlayedPostfix(BlackHolePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitBlackHoleTaskAsync(__result);
    }

    public static void BlackHoleAfterStarsGainedPrefix(BlackHolePower __instance)
    {
        CardRegistry.IsBlackHoleExecuting.Value = true;
    }

    public static void BlackHoleAfterStarsGainedPostfix(BlackHolePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitBlackHoleTaskAsync(__result);
    }

    public static void SleightOfFleshAfterPowerAmountChangedPrefix(SleightOfFleshPower __instance)
    {
        CardRegistry.IsSleightOfFleshExecuting.Value = true;
    }

    public static void SleightOfFleshAfterPowerAmountChangedPostfix(SleightOfFleshPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitSleightOfFleshTaskAsync(__result);
    }
    
    // --- ORB WRAPPERS ---

    public static void OrbChannelPostfix(PlayerChoiceContext choiceContext, OrbModel orb, MegaCrit.Sts2.Core.Entities.Players.Player player)
    {
        CardRegistry.RegisterChanneledOrb(orb, CardRegistry.CurrentPlayingCard);
    }

    public static void OrbPassivePrefix(OrbModel __instance)
    {
        string? forcingActor = null;

        // If Loop is running, pop the next card in line!
        if (CardRegistry.IsLoopExecuting.Value && CardRegistry.CurrentTurnLoopQueue.Count > 0)
        {
            forcingActor = CardRegistry.CurrentTurnLoopQueue[0];
            CardRegistry.CurrentTurnLoopQueue.RemoveAt(0); // Pop!
        }
        // Otherwise, if Darkness is being played, it gets the credit!
        else if (CardRegistry.CurrentPlayingCard != null)
        {
            forcingActor = CardRegistry.GetTrackingId(CardRegistry.CurrentPlayingCard);
        }

        // Bake the Forcing Actor directly into the execution context so the Waterfall doesn't have to guess!
        CardRegistry.ExecutingOrb.Value = new OrbExecutionContext(__instance, false, __instance.PassiveVal, forcingActor);
    }
    
    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
    }

    public static void OrbEvokePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] Trap SET for {__instance.Id.Entry} Evoke");
        // Cache the EvokeVal before the execution!
        CardRegistry.ExecutingOrb.Value = new OrbExecutionContext(__instance, true, __instance.EvokeVal);
    }
    
    public static void OrbEvokePostfix(OrbModel __instance, ref Task<IEnumerable<Creature>> __result)
    {
        // Smoothly wraps the Task<T> without losing the IEnumerable<Creature> return value!
        __result = CardRegistry.AwaitOrbEvokeTaskAsync(__result, __instance);
    }
    
    public static void TempFocusApplyPrefix(TemporaryFocusPower __instance)
    {
        CardRegistry.IsApplyingTemporaryFocus.Value = true;
    }

    public static void TempFocusApplyPostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusApplyAsync(__result);
    }

    public static void TempFocusExpirePrefix(TemporaryFocusPower __instance)
    {
        CardRegistry.IsExpiringTemporaryFocus.Value = true;
    }

    public static void TempFocusExpirePostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusExpireAsync(__result);
    }
    
    public static void LoopPrefix(LoopPower __instance)
    {
        CardRegistry.IsLoopExecuting.Value = true;
        CardRegistry.CurrentTurnLoopQueue.Clear();
        
        // Flatten the ledger into an execution queue (FIFO)
        lock (CardRegistry.SyncRoot)
        {
            foreach (var contribution in CardRegistry.LoopHistory)
            {
                // If Card A gave 2 Loop, add its ID twice. If Card B gave 1, add it once.
                for (int i = 0; i < contribution.Amount; i++)
                {
                    CardRegistry.CurrentTurnLoopQueue.Add(contribution.TrackingId);
                }
            }
        }
    }

    public static void LoopPostfix(LoopPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitLoopTaskAsync(__result);
    }
    
    // Catches all damage dealt
    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        GD.Print($"[DeckTracker] AfterDamageGivePostfix triggered");
        if (CardRegistry.CurrentPoisonTarget.Value == target && results.TotalDamage > 0)
        {
            GD.Print($"[DeckTracker] Poison detected");
            CardRegistry.DistributePoisonDamage(target, results.TotalDamage);
            
            if (!target.IsAlive) 
                CardRegistry.ClearStateForTarget(target);
                
            return;
        }
        
        // ORB INTERCEPT
        if (CardRegistry.ExecutingOrb.Value != null && results.TotalDamage > 0)
        {
            Creature player = combatState.Players[0].Creature;
            CardRegistry.DistributeOrbDamage(CardRegistry.ExecutingOrb.Value, results.TotalDamage, player);
            return; 
        }

        if (CardRegistry.IsStrangleExecuting.Value && results.TotalDamage > 0)
        {
            CardRegistry.DistributeStrangleDamage(target, results.TotalDamage);
            return;
        }

        if (CardRegistry.IsSerpentFormExecuting.Value && results.TotalDamage > 0)
        {
            CardRegistry.DistributeSerpentFormDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsBlackHoleExecuting.Value && results.TotalDamage > 0)
        {
            CardRegistry.DistributeBlackHoleDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsSleightOfFleshExecuting.Value && results.TotalDamage > 0)
        {
            CardRegistry.DistributeSleightOfFleshDamage(results.TotalDamage);
            return;
        }

        if (cardSource == null)
        {
            GD.Print($"[DeckTracker] CardSource is null and not poison or supported orb. Returning..." +
                     $"with value: {CardRegistry.ExecutingOrb.Value} and damage: {results.TotalDamage}");
            return;
        }
        
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

    private static int ExtractActNum(IRunState? runState)
    {
        if (runState == null) return 1;
        // CurrentActIndex is 0-based (Act 1 = 0), so we add 1 to match our 1-based registry.
        return runState.CurrentActIndex + 1;
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