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
    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy)
    {
        // We only care if we are actively in combat
        if (combatState == null) return;
        
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

    public static void AfterSideTurnStartPostfix(
        ICombatState combatState,
        CombatSide side,
        IReadOnlyList<Creature> participants)
    {
        CardRegistry.ResetOrbTurnState();
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
    
    public static void ModifyDamagePostfix(
        Decimal damage,
        Creature? target,
        Creature? dealer,
        ValueProp props,
        CardModel? cardSource,
        CardPreviewMode previewMode,
        ref IEnumerable<AbstractModel> modifiers,
        ref Decimal __result)
    {
        // FILTER: Completely ignore UI previews and phantom calculations!
        if (previewMode != CardPreviewMode.None) return;

        var snapshot = new CardRegistry.DamageSnapshot
        {
            BaseDamage = damage, 
            CardSource = cardSource,
            Target = target,
            Props = props,
            Dealer = dealer
        };

        foreach (var mod in modifiers)
        {
            // We call the math directly on the base AbstractModel!
            decimal addAmount = mod.ModifyDamageAdditive(target, damage, props, dealer, cardSource);
            if (addAmount > 0)
            {
                snapshot.AdditiveModifiers.Add(new CardRegistry.DamageModifierSnapshot {
                    PowerId = mod.Id.Entry,
                    Amount = addAmount
                });
            }

            // Extract the Multiplier
            decimal multAmount = mod.ModifyDamageMultiplicative(target, damage, props, dealer, cardSource);
            
            // We ignore 1m (no change) and 0m (just in case of a weird engine fail-safe)
            if (multAmount != 1m && multAmount != 0m)
            {
                snapshot.MultiplicativeModifiers.Add(new CardRegistry.DamageModifierSnapshot {
                    PowerId = mod.Id.Entry,
                    Amount = multAmount,
                    PowerInstance = mod as PowerModel
                });
                GD.Print($"[DeckTracker] Logged Multiplier: {mod.Id.Entry} with {multAmount}x");
            }
        }
        CardRegistry.CurrentAttackSnapshot.Value = snapshot;
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
                break;
            case OblivionPower:
                if (amount > 0) CardRegistry.LogOblivionApply(target, cardSource, (int)amount);
                break;
            case SerpentFormPower:
                if (amount > 0) CardRegistry.LogSerpentFormApply(cardSource, (int)amount);
                break;
            case ReaperFormPower:
                if (amount > 0) CardRegistry.AddReaperFormShares(amount, cardSource);
                break;
            case BlackHolePower:
                if (amount > 0) CardRegistry.LogBlackHoleApply(cardSource, (int)amount);
                break;
            case SleightOfFleshPower:
                if (amount > 0) CardRegistry.LogSleightOfFleshApply(cardSource, (int)amount);
                break;
            case HauntPower:
                if (amount > 0) CardRegistry.LogHauntApply(cardSource, (int)amount);
                break;
            case SpeedsterPower:
                if (amount > 0) CardRegistry.LogSpeedsterApply(cardSource, (int)amount);
                break;
            case ThunderPower:
                if (amount > 0) CardRegistry.LogThunderApply(cardSource, (int)amount);
                break;
            case StormPower:
                if (amount > 0) CardRegistry.LogStormApply(cardSource, (int)amount);
                break;
            case HailstormPower:
                if (amount > 0) CardRegistry.LogHailstormApply(cardSource, (int)amount);
                break;
            case JuggernautPower:
                if (amount > 0) CardRegistry.LogJuggernautApply(cardSource, (int)amount);
                break;
            case NecroMasteryPower:
                if (amount > 0) CardRegistry.LogNecroMasteryApply(cardSource, (int)amount);
                break;
            case RollingBoulderPower:
                CardRegistry.LogRollingBoulderInstance(power, cardSource);
                break;
            case ThornsPower:
                if (amount > 0 && target.IsPlayer)
                {
                    CardRegistry.LogThornsApply(cardSource, (int)amount);
                }
                break;
            case FlameBarrierPower:
                if (amount > 0) CardRegistry.LogFlameBarrierApply(cardSource, (int)amount);
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
                    // This is the BASE amount requested by the card/source (e.g., 5)
                    decimal amountToCreditToSourcePoison = amount;

                    // 1. PASSIVE MODIFIERS (e.g., Snecko Skull)
                    if (RelicExecutionManager.PendingPowerModifiers.Value != null && RelicExecutionManager.PendingPowerModifiers.Value.Count > 0)
                    {
                        var keysToRemove = new List<string>();
                        foreach (var kvp in RelicExecutionManager.PendingPowerModifiers.Value)
                        {
                            if (kvp.Value.powerType == "POISON_POWER")
                            {
                                // Credit Snecko Skull with the +1 Poison it generated!
                                CardRegistry.AddPoisonSharesById(target, kvp.Value.delta, "RELIC_" + kvp.Key);
                                
                                // CRITICAL FIX: Do NOT subtract the delta from the source amount. 
                                // The hook 'amount' is already the un-modified base!
                                
                                GD.Print($"[DeckTracker] Attributed delta {kvp.Value.delta} to {kvp.Key}");
                                keysToRemove.Add(kvp.Key); 
                            }
                        }
                        
                        foreach (var key in keysToRemove)
                        {
                            RelicExecutionManager.PendingPowerModifiers.Value.Remove(key);
                        }
                    }
                    
                    GD.Print($"[DeckTracker] {amountToCreditToSourcePoison} poison left to attribute to source.");
                    
                    // 2. MIDDLEMEN MODIFIERS
                    if (CardRegistry.IsNoxiousFumesExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalFumes = CardRegistry.FumesShares.Sum(x => x.Shares);
                            if (totalFumes > 0)
                            {
                                foreach (var share in CardRegistry.FumesShares)
                                {
                                    decimal proportion = share.Shares / totalFumes;
                                    CardRegistry.AddPoisonSharesById(target, amountToCreditToSourcePoison * proportion, share.TrackingId);
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
                                    CardRegistry.AddPoisonSharesById(target, amountToCreditToSourcePoison * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    // Envenom
                    else if (CardRegistry.IsEnvenomExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal totalEnvenom = CardRegistry.EnvenomShares.Sum(x => x.Shares);
                            if (totalEnvenom > 0)
                            {
                                foreach (var share in CardRegistry.EnvenomShares)
                                {
                                    decimal proportion = share.Shares / totalEnvenom;
                                    CardRegistry.AddPoisonSharesById(target, amountToCreditToSourcePoison * proportion, share.TrackingId);
                                }
                            }
                        }
                    }
                    else
                    {
                        // 3. BASE SOURCES
                        GD.Print($"[DeckTracker] Adding poison power {amountToCreditToSourcePoison} from card {cardSource?.Id.Entry}.");
                        if (cardSource == null && !string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
                        {
                            CardRegistry.AddPoisonSharesById(target, amountToCreditToSourcePoison, "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value);
                        }
                        else
                        {
                            CardRegistry.AddPoisonShares(target, amountToCreditToSourcePoison, cardSource);
                        }
                    }
                }
                else if (amount < 0)
                {
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
                    else if (CardRegistry.IsReaperFormExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal remainingDoom = amount;
                            decimal damageDealt = CardRegistry.GetReaperDamage();

                            // Read the Reaper Form history top-to-bottom and assign the Doom
                            foreach (var share in CardRegistry.ReaperFormShares)
                            {
                                if (remainingDoom <= 0) break;
                                
                                // Reaper Form multiplier: share.Shares * damageDealt
                                decimal amountToAttribute = Math.Min(remainingDoom, share.Shares * damageDealt);
                                CardRegistry.AddDoomHistoryById(target, amountToAttribute, share.TrackingId);

                                remainingDoom -= amountToAttribute;
                            }
                        }
                    }
                    else if (CardRegistry.IsOblivionExecuting)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal remainingDoom = amount;

                            if (CardRegistry.OblivionLedgers.TryGetValue(target, out var ledger))
                            {
                                foreach (var share in ledger)
                                {
                                    if (remainingDoom <= 0) break;

                                    decimal amountToAttribute = Math.Min(remainingDoom, (decimal)share.Amount);
                                    CardRegistry.AddDoomHistoryById(target, amountToAttribute, share.TrackingId);

                                    remainingDoom -= amountToAttribute;
                                }
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
            case ReflectPower:
                if (amount > 0 && cardSource != null)
                {
                    for (int i = 0; i < amount; i++)
                        CardRegistry.AddReflect(CardRegistry.GetTrackingId(cardSource));
                }
                else if (amount < 0)
                {
                    for (int i = 0; i > amount; i--)
                        CardRegistry.DecrementReflect();
                }
                break;
            case DemonFormPower:
                if (target.IsPlayer)
                {
                    if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                    else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                }
                break;
            case ArsenalPower:
                if (target.IsPlayer)
                {
                    if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                    else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                }
                break;
            case MonologuePower:
                if (target.IsPlayer)
                {
                    if (amount > 0) 
                    {
                        // TAPE THE NAMETAG: Link this specific power object to the card source!
                        CardRegistry.InstancedPowerSources[power] = CardRegistry.GetTrackingId(cardSource);
                    }
                    else if (amount < 0) 
                    {
                        // Cleanup when the power is removed
                        CardRegistry.InstancedPowerSources.Remove(power);
                    }
                }
                break;
            case StrengthPower:
                if (!target.IsPlayer) break;
                decimal amountToCreditToSource = amount;

                // 1. PASSIVE MODIFIERS (e.g., Ruined Helmet)
                if (RelicExecutionManager.PendingPowerModifiers.Value != null && RelicExecutionManager.PendingPowerModifiers.Value.Count > 0)
                {
                    GD.Print($"[DeckTracker] StrengthPower Part 1");
                    var keysToRemove = new List<string>();
                    foreach (var kvp in RelicExecutionManager.PendingPowerModifiers.Value) 
                    {
                        if (kvp.Value.powerType == "STRENGTH_POWER")
                        {
                            // Credit the Relic with the bonus it generated!
                            CardRegistry.AddPersistentBuffById(power.Id.Entry, kvp.Value.delta, "RELIC_" + kvp.Key);
                            keysToRemove.Add(kvp.Key);
                        }
                    }
                    
                    // Clean up only the modifiers we actually consumed
                    foreach (var key in keysToRemove)
                    {
                        RelicExecutionManager.PendingPowerModifiers.Value.Remove(key);
                    }
                }
                
                // 2. PROCESS THE REMAINDER (Cards, Active Relics, or Middlemen)
                if (amountToCreditToSource > 0)
                {
                    GD.Print($"[DeckTracker] StrengthPower Part 1");
                    if (CardRegistry.IsDemonFormExecuting.Value) 
                        CardRegistry.ProcessDemonFormStrength(amountToCreditToSource);
                    else if (CardRegistry.IsArsenalExecuting.Value) 
                        CardRegistry.ProcessArsenalStrength(amountToCreditToSource);
                    else if (!string.IsNullOrEmpty(CardRegistry.ExecutingInstancedSource.Value)) 
                        CardRegistry.AddPersistentBuffById(power.Id.Entry, amountToCreditToSource, CardRegistry.ExecutingInstancedSource.Value);
                    
                    // NEW: Active Relic Appliers (Vajra, Mini Regent)
                    else if (cardSource == null && !string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
                    {
                        GD.Print($"[DeckTracker] StrengthPower Inside relic check");
                        CardRegistry.AddPersistentBuffById(power.Id.Entry, amountToCreditToSource, "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value);
                    }
                    else
                    {
                        GD.Print($"[DeckTracker] StrengthPower Final Card Source");
                        CardRegistry.AddPersistentBuff(power.Id.Entry, amountToCreditToSource, cardSource);
                    }
                }
                else if (amountToCreditToSource < 0) 
                {
                    // Handles Monologue cleanup, Red Skull deactivation, Enemy debuffs, etc.
                    CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amountToCreditToSource));
                }
                break;
            case AccuracyPower:
                if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                break;
            case PhantomBladesPower:
                if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                break;
            case PrepTimePower:
                if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                break;
            case VigorPower:
                if (amount > 0)
                {
                    // INTERCEPT: Did PrepTimePower generate this Vigor?
                    if (CardRegistry.IsPrepTimeExecuting.Value)
                    {
                        CardRegistry.ProcessPrepTimeVigor(amount);
                    }
                    // NEW: Intercept Akabeko and any future Vigor relics!
                    else if (cardSource == null && !string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
                    {
                        CardRegistry.AddConsumableBuffById(power.Id.Entry, amount, "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value);
                    }
                    else
                    {
                        CardRegistry.AddConsumableBuff(power.Id.Entry, amount, cardSource);
                    }
                }
                else if (amount < 0) CardRegistry.RemoveConsumableBuff(power.Id.Entry, Math.Abs(amount));
                break;
            case VulnerablePower:
                // Note we changed AddEnemyDebuff to AddDurationBuff
                if (amount > 0) CardRegistry.AddDurationBuff(target, power.Id.Entry, amount, CardRegistry.GetTrackingId(cardSource));
                else if (amount < 0) CardRegistry.RemoveDurationBuff(target, power.Id.Entry, Math.Abs(amount));
                break;
            case ShadowStepPower:
                if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                break;

            case DoubleDamagePower:
                if (amount > 0)
                {
                    if (CardRegistry.IsShadowStepExecuting.Value)
                    {
                        CardRegistry.ProcessShadowStepDoubleDamage(amount, target);
                    }
                    else
                    {
                        CardRegistry.AddDurationBuff(target, power.Id.Entry, amount, CardRegistry.GetTrackingId(cardSource));
                    }
                }
                else if (amount < 0) 
                {
                    CardRegistry.RemoveDurationBuff(target, power.Id.Entry, Math.Abs(amount));
                }
                break;
            case TrackingPower:
                if (amount > 0) 
                {
                    // Check if this is the very first time Tracking is being applied
                    bool isFirstApplication = !CardRegistry.PersistentLedgers.ContainsKey(power.Id.Entry) || 
                                              CardRegistry.PersistentLedgers[power.Id.Entry].Count == 0;
                
                    // The first application gives 2 stacks, but 1 of those is the inherent 1.0x base.
                    // We only want to log the actual BONUS multiplier delta into the ledger!
                    decimal loggedAmount = isFirstApplication ? amount - 1 : amount;
                
                    if (loggedAmount > 0) 
                    {
                        CardRegistry.AddPersistentBuff(power.Id.Entry, loggedAmount, cardSource);
                    }
                }
                else if (amount < 0) 
                {
                    // If it ever gets removed or completely wiped, we wipe the ledger as usual.
                    // (Using a full wipe here is safest if a boss cleanses debuffs/buffs)
                    CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                }
                break;
            case EnvenomPower:
                if (target.IsPlayer)
                {
                    if (amount > 0)
                    {
                        CardRegistry.AddEnvenomShares(amount, cardSource);
                    }
                }
                break;
            case TrashToTreasurePower:
                if (target.IsPlayer)
                {
                    if (amount > 0) CardRegistry.AddTrashToTreasureShares(amount, cardSource);
                }
                break;
            // Cruelty acts like Strength (stacks infinitely, persists on Player)
            case CrueltyPower:
                if (target != null && target.IsPlayer)
                {
                    if (amount > 0) CardRegistry.AddPersistentBuff(power.Id.Entry, amount, cardSource);
                    else if (amount < 0) CardRegistry.RemovePersistentBuff(power.Id.Entry, Math.Abs(amount));
                }
                break;

            // Debilitate acts like Weak (duration counts down, FIFO queue on Enemy)
            case DebilitatePower:
                if (amount > 0) CardRegistry.AddDurationBuff(target, power.Id.Entry, amount, CardRegistry.GetTrackingId(cardSource));
                else if (amount < 0) CardRegistry.RemoveDurationBuff(target, power.Id.Entry, Math.Abs(amount));
                break;
            case FlankingPower:
                // let FlankingPower and KnockdownPower share the same code
            case KnockdownPower:
                if (amount > 0) 
                {
                    CardRegistry.InstancedPowerSources[power] = CardRegistry.GetTrackingId(cardSource);
                    GD.Print($"[DeckTracker] Mapped Instanced {power.Id.Entry} to {CardRegistry.GetTrackingId(cardSource)}");
                }
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
        else if (cardId.Equals("FAN_OF_KNIVES"))
        {
            CardRegistry.UpdateFanOfKnives(cardPlay.Card);
        }
        else if (cardId.Equals("SOVEREIGN_BLADE"))
        {
            CardRegistry.ProcessSovereignBladeHistory(cardPlay);
        }
        else if (cardId.Equals("SHIV"))
        {
            CardRegistry.ProcessShivHistory(cardPlay);
            CardRegistry.CurrentAttackSnapshot.Value = null;
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
        else if (source is RelicModel relic)
        {
            CardRegistry.RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
            CardRegistry.AddForgeById("RELIC_" + relic.Id.Entry, amount);
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
        CardRegistry.StartStrangleExecution();
    }

    public static void StrangleAfterCardPlayedPostfix(StranglePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitStrangleTaskAsync(__result, __instance.Owner, __instance.Amount);
    }

    public static void OblivionAfterCardPlayedPrefix(OblivionPower __instance)
    {
        CardRegistry.StartOblivionExecution();
    }

    public static void OblivionAfterCardPlayedPostfix(OblivionPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitOblivionTaskAsync(__result);
    }

    public static void SerpentFormAfterCardPlayedPrefix(SerpentFormPower __instance)
    {
        CardRegistry.StartSerpentFormExecution();
    }

    public static void SerpentFormAfterCardPlayedPostfix(SerpentFormPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitSerpentFormTaskAsync(__result);
    }

    public static void ReaperFormAfterDamageGivenPrefix(ReaperFormPower __instance, DamageResult result)
    {
        CardRegistry.StartReaperFormExecution(result.TotalDamage);
    }

    public static void ReaperFormAfterDamageGivenPostfix(ReaperFormPower __instance, ref Task __result, DamageResult result)
    {
        __result = CardRegistry.AwaitReaperFormTaskAsync(__result, result.TotalDamage);
    }

    public static void BlackHoleAfterCardPlayedPrefix(BlackHolePower __instance)
    {
        CardRegistry.StartBlackHoleExecution();
    }

    public static void BlackHoleAfterCardPlayedPostfix(BlackHolePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitBlackHoleTaskAsync(__result);
    }

    public static void BlackHoleAfterStarsGainedPrefix(BlackHolePower __instance)
    {
        CardRegistry.StartBlackHoleExecution();
    }

    public static void BlackHoleAfterStarsGainedPostfix(BlackHolePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitBlackHoleTaskAsync(__result);
    }

    public static void SleightOfFleshAfterPowerAmountChangedPrefix(SleightOfFleshPower __instance)
    {
        CardRegistry.StartSleightOfFleshExecution();
    }

    public static void SleightOfFleshAfterPowerAmountChangedPostfix(SleightOfFleshPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitSleightOfFleshTaskAsync(__result);
    }

    public static void HauntAfterCardPlayedPrefix(HauntPower __instance)
    {
        CardRegistry.StartHauntExecution();
    }

    public static void HauntAfterCardPlayedPostfix(HauntPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitHauntTaskAsync(__result);
    }

    public static void SpeedsterAfterCardDrawnPrefix(SpeedsterPower __instance)
    {
        CardRegistry.StartSpeedsterExecution();
    }

    public static void SpeedsterAfterCardDrawnPostfix(SpeedsterPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitSpeedsterTaskAsync(__result);
    }

    public static void ThunderAfterOrbEvokedPrefix(ThunderPower __instance)
    {
        CardRegistry.StartThunderExecution();
    }

    public static void ThunderAfterOrbEvokedPostfix(ThunderPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitThunderTaskAsync(__result);
    }

    public static void StormAfterCardPlayedPrefix(StormPower __instance)
    {
        CardRegistry.IsStormExecuting.Value = true;
        CardRegistry.CurrentTurnStormQueue.Clear();
        lock (CardRegistry.SyncRoot)
        {
            foreach (var contribution in CardRegistry.StormHistory)
            {
                for (int i = 0; i < contribution.Amount; i++)
                {
                    CardRegistry.CurrentTurnStormQueue.Add(contribution.TrackingId);
                }
            }
        }
    }

    public static void StormAfterCardPlayedPostfix(StormPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitStormTaskAsync(__result);
    }

    public static void HailstormBeforeTurnEndPrefix(HailstormPower __instance)
    {
        CardRegistry.StartHailstormExecution();
    }

    public static void HailstormBeforeTurnEndPostfix(HailstormPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitHailstormTaskAsync(__result);
    }

    public static void JuggernautAfterBlockGainedPrefix(JuggernautPower __instance)
    {
        CardRegistry.StartJuggernautExecution();
    }

    public static void JuggernautAfterBlockGainedPostfix(JuggernautPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitJuggernautTaskAsync(__result);
    }

    public static void NecroMasteryAfterCurrentHpChangedPrefix(NecroMasteryPower __instance, decimal delta )
    {
        CardRegistry.StartNecroMasteryExecution(delta);
    }

    public static void NecroMasteryAfterCurrentHpChangedPostfix(NecroMasteryPower __instance, ref Task __result, decimal delta)
    {
        __result = CardRegistry.AwaitNecroMasteryTaskAsync(__result, delta);
    }

    public static void ThornsBeforeDamageReceivedPrefix(ThornsPower __instance)
    {
        CardRegistry.StartThornsExecution();
    }

    public static void ThornsBeforeDamageReceivedPostfix(ThornsPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitThornsTaskAsync(__result);
    }

    public static void FlameBarrierAfterDamageReceivedPrefix(FlameBarrierPower __instance)
    {
        CardRegistry.StartFlameBarrierExecution();
    }

    public static void FlameBarrierAfterDamageReceivedPostfix(FlameBarrierPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitFlameBarrierTaskAsync(__result);
    }
    
    public static void ReflectAfterDamageReceivedPrefix(ReflectPower __instance)
    {
        CardRegistry.StartReflectExecution();
    }

    public static void ReflectAfterDamageReceivedPostfix(ReflectPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitReflectTaskAsync(__result);
    }
    
    public static void BeforePowerRemovedPrefix(PowerModel? power)
    {
        if (power == null) return;
        
        GD.Print($"[DeckTracker] BeforePowerRemovedPrefix: {power.GetType().Name} removed from {power.Owner.Name}");
        
        switch (power)
        {
            case FlameBarrierPower:
                CardRegistry.ClearFlameBarrier();
                break;
            case StranglePower:
                CardRegistry.ClearStrangle(power.Owner);
                break;
            case OblivionPower:
                CardRegistry.ClearOblivion(power.Owner);
                break;
        }
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
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            // Catch Emotion Chip!
            forcingActor = "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
        }
        // Otherwise, if Darkness is being played, it gets the credit!
        else if (CardRegistry.CurrentPlayingCard != null)
        {
            forcingActor = CardRegistry.GetTrackingId(CardRegistry.CurrentPlayingCard);
        }
        // 4. Natural End-of-Turn Passive
        else
        {
            lock (CardRegistry.SyncRoot)
            {
                // Increment the counter for this specific orb
                int count = CardRegistry.EotPassiveCounts.GetValueOrDefault(__instance, 0) + 1;
                CardRegistry.EotPassiveCounts[__instance] = count;

                // The first execution is the natural one (credit to Channeler). 
                // Any execution after the first is the extra loop from Gold Plated Cables!
                if (count > 1)
                {
                    forcingActor = "RELIC_GoldPlatedCables";
                }
            }
        }
        // Bake the Forcing Actor directly into the execution context so the Waterfall doesn't have to guess!
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, false, __instance.PassiveVal, forcingActor);
    }
    
    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
    }

    public static void OrbEvokePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] Trap SET for {__instance.Id.Entry} Evoke");
        // Cache the EvokeVal before the execution!
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, true, __instance.EvokeVal);
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

    public static void RollingBoulderAfterPlayerTurnStartPrefix(RollingBoulderPower __instance)
    {
        CardRegistry.StartRollingBoulderExecution(__instance);
    }

    public static void RollingBoulderAfterPlayerTurnStartPostfix(RollingBoulderPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitRollingBoulderTaskAsync(__result, __instance);
    }
    
    public static void PrepTimePrefix(PrepTimePower __instance)
    {
        CardRegistry.IsPrepTimeExecuting.Value = true;
    }

    public static void PrepTimePostfix(PrepTimePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitPrepTimeTaskAsync(__result);
    }
    
    // --- SHADOW STEP EXECUTION TRAP ---

    public static void ShadowStepPrefix(ShadowStepPower __instance)
    {
        CardRegistry.IsShadowStepExecuting.Value = true;
    }

    public static void ShadowStepPostfix(ShadowStepPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitShadowStepTaskAsync(__result);
    }
    
    public static void DemonFormPrefix(DemonFormPower __instance)
    {
        CardRegistry.IsDemonFormExecuting.Value = true;
    }

    public static void DemonFormPostfix(DemonFormPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitDemonFormTaskAsync(__result);
    }
    
    public static void ArsenalPrefix(ArsenalPower __instance)
    {
        CardRegistry.IsArsenalExecuting.Value = true;
    }

    public static void ArsenalPostfix(ArsenalPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitArsenalTaskAsync(__result);
    }
    
    // --- MONOLOGUE EXECUTION TRAP ---

    public static void MonologuePrefix(MonologuePower __instance)
    {
        // Read the nametag! If this instance has one, tell the system who is executing.
        if (CardRegistry.InstancedPowerSources.TryGetValue(__instance, out var sourceId))
        {
            CardRegistry.ExecutingInstancedSource.Value = sourceId;
        }
    }

    public static void MonologuePostfix(MonologuePower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitInstancedTaskAsync(__result);
    }
    
    // A Prefix to open the trap right before Envenom executes
    public static void EnvenomPrefix()
    {
        CardRegistry.IsEnvenomExecuting.Value = true;
    }

    // A Postfix to wrap the async task and close the trap when it finishes applying poison
    public static void EnvenomPostfix(ref Task __result)
    {
        async Task WrappedTask(Task originalTask)
        {
            try { await originalTask; }
            finally { CardRegistry.IsEnvenomExecuting.Value = false; }
        }
        
        __result = WrappedTask(__result);
    }
    
    // Opens the trap and builds the attribution deck!
    public static void TrashToTreasurePrefix(TrashToTreasurePower __instance)
    {
        CardRegistry.IsTrashToTreasureExecuting.Value = true;
        CardRegistry.TrashToTreasureAttributionQueue.Value = new Queue<string>();
        
        lock (CardRegistry.SyncRoot)
        {
            // If Card A gave 1 stack and Card B gave 2 stacks, this loop builds a queue: [CardA, CardB, CardB]
            foreach (var share in CardRegistry.TrashToTreasureShares)
            {
                int wholeShares = (int)Math.Round(share.Shares);
                for (int i = 0; i < wholeShares; i++)
                {
                    CardRegistry.TrashToTreasureAttributionQueue.Value.Enqueue(share.TrackingId);
                }
            }
        }
    }

    // Closes the trap
    public static void TrashToTreasurePostfix(ref Task __result)
    {
        async Task WrappedTask(Task originalTask)
        {
            try { await originalTask; }
            finally { CardRegistry.IsTrashToTreasureExecuting.Value = false; }
        }
        __result = WrappedTask(__result);
    }
    
    public static void PlayerAddRelicPostfix(RelicModel relic)
    {
        if (relic != null)
        {
            CardRegistry.RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
            Godot.GD.Print($"[DeckTracker] Cached localized name for {relic.Id.Entry}: {CardRegistry.RelicNameCache[relic.Id.Entry]}");
        }
    }
    
    // Catches all damage dealt
    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        GD.Print($"[DeckTracker] AfterDamageGivePostfix triggered");
        if (cardSource == null && !string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            string executingRelic = RelicExecutionManager.ExecutingRelicId.Value;
            CardRegistry.AddRelicDamage(executingRelic, results.TotalDamage);
            return; 
        }
        
        if (CardRegistry.CurrentPoisonTarget.Value == target && results.TotalDamage > 0)
        {
            GD.Print($"[DeckTracker] Poison detected");
            CardRegistry.DistributePoisonDamage(target, results.TotalDamage);
            
            if (!target.IsAlive) 
                CardRegistry.ClearStateForTarget(target);
                
            return;
        }
        
        // ORB INTERCEPT
        if (CardRegistry.ExecutingOrb != null && results.TotalDamage > 0)
        {
            Creature player = combatState.Players[0].Creature;
            CardRegistry.DistributeOrbDamage(CardRegistry.ExecutingOrb, results.TotalDamage, player);
            return; 
        }

        if (CardRegistry.IsStrangleExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeStrangleDamage(target, results.TotalDamage);
            return;
        }

        if (CardRegistry.IsSerpentFormExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeSerpentFormDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsBlackHoleExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeBlackHoleDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsSleightOfFleshExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeSleightOfFleshDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsHauntExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeHauntDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsHailstormExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeHailstormDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsSpeedsterExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeSpeedsterDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsThunderExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeThunderDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsJuggernautExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeJuggernautDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsNecroMasteryExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeNecroMasteryDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsThornsExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeThornsDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsFlameBarrierExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeFlameBarrierDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.IsReflectExecuting && results.TotalDamage > 0)
        {
            CardRegistry.DistributeReflectDamage(results.TotalDamage);
            return;
        }

        if (CardRegistry.ExecutingBoulder != null && results.TotalDamage > 0)
        {
            CardRegistry.DistributeRollingBoulderDamage(results.TotalDamage);
            return;
        }

        if (cardSource == null)
        {
            GD.Print($"[DeckTracker] CardSource is null and not poison or supported orb. Returning..." +
                     $"with value: {CardRegistry.ExecutingOrb} and damage: {results.TotalDamage}");
            return;
        }
        
        decimal baseCardDamage = results.TotalDamage;
        if (CardRegistry.CurrentAttackSnapshot.Value != null && 
            CardRegistry.CurrentAttackSnapshot.Value.CardSource == cardSource)
        {
            baseCardDamage = CardRegistry.ProcessDamageSnapshot(CardRegistry.CurrentAttackSnapshot.Value, results.TotalDamage);
        }
        
        // If the card is Sovereign Blade, process the forge distribution!
        GD.Print($"[DeckTracker] Card {cardSource.Id.Entry} did {results.TotalDamage} damage to {target.Name} with target type {cardSource.TargetType}");
        GD.Print($"[DeckTracker] {combatState.Enemies.Count} enemies in the combat via after damage");
        if (cardSource.Id.Entry.Equals("SOVEREIGN_BLADE")) 
        {
            var damageHistoryItem = new DamageHistoryItem(combatState, dealer, results, target, cardSource);
            CardRegistry.AddSovereignBladeDamageHistoryItem(damageHistoryItem);
        }
        else if (cardSource.Id.Entry.Equals("SHIV"))
        {
            // Bypass the un-peeled results object and pass the clean base damage!
            CardRegistry.AddShivDamage(cardSource, baseCardDamage);
        }
        else
        {
            CardRegistry.AddDamage(cardSource, baseCardDamage); 
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