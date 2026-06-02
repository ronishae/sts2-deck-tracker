using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
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
        GD.Print($"[DeckTracker] AfterRoomEnteredPostfix. Floor: {currentFloor}, Room: {room.RoomType}");
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
    }
    
    public static void BeforeCardRemovedPostfix(IRunState runState, CardModel card)
    {
        var currentFloor = ExtractFloorNum(runState);
        GD.Print($"[DeckTracker] BeforeCardRemovedPostfix. Card: {card.Id.Entry}, Floor: {currentFloor}");
        CardRegistry.HandleRemove(card, currentFloor);
    }
    
    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy)
    {
        if (combatState == null)
        {
            return;
        }
        try
        {
            if (card.Pile != null && card.Pile.Type == PileType.Hand && oldPile != PileType.Draw)
            {
                if (CardRegistry.IsCardPlayActive())
                {
                    GD.Print($"[DeckTracker] AfterCardChangedPilesPostfix. Deferring draw for {card.Id.Entry}");
                    CardRegistry.DeferDraw(card);
                }
                else
                {
                    GD.Print($"[DeckTracker] AfterCardChangedPilesPostfix. Direct draw for {card.Id.Entry}");
                    CardRegistry.RegisterCard(card);
                    CardRegistry.AddDraw(card);
                    CardRegistry.ForcePublish();
                }
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[DeckTracker] AfterCardChangedPilesPostfix Error: {e.Message}");
        }
    }
    
    public static void BeforeRoomEnteredPrefix(IRunState? runState, AbstractRoom room)
    {
        var seed = ExtractRunSeed(runState);
        GD.Print($"[DeckTracker] BeforeRoomEnteredPrefix. Seed: {seed}, Room: {room.RoomType}");
        CardRegistry.SyncRun(seed); 
    }
    
    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
    {
        var currentFloor = ExtractFloorNum(runState);
        var currentAct = ExtractActNum(runState);
        var combatType = GetCombatType(runState);
        var activeDeckIds = ScanDeckForCards(runState);
        
        GD.Print($"[DeckTracker] BeforeCombatStartPostfix. Floor: {currentFloor}, Act: {currentAct}, Type: {combatType}");
        
        CardRegistry.StartCombat(combatType, currentFloor, currentAct, activeDeckIds);
        CardRegistry.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    }

    public static void AfterSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        GD.Print($"[DeckTracker] AfterSideTurnStartPostfix. Side: {side}");
        CardRegistry.ResetOrbTurnState();
    }

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState)
    {
        GD.Print("[DeckTracker] AfterCombatEndPostfix.");
        CardRegistry.ProcessCombatEnd();
    }

    public static void AfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw)
    {
        GD.Print($"[DeckTracker] AfterCardDrawnPostfix. Card: {card.Id.Entry}, FromHand: {fromHandDraw}");
        CardRegistry.RegisterCard(card);
        CardRegistry.AddDraw(card);
    }

    public static void BeforeCardPlayedPostfix(ICombatState combatState, CardPlay cardPlay)
    {
        try
        {
            var cardProp = cardPlay.GetType().GetProperty("Card");
            if (cardProp?.GetValue(cardPlay) is CardModel card)
            {
                GD.Print($"[DeckTracker] BeforeCardPlayedPostfix. Card: {card.Id.Entry}, PlayIndex: {cardPlay.PlayIndex}");
                CardRegistry.StartCardPlay(card);
                CardRegistry.RegisterCard(card);
                if (cardPlay.PlayIndex == 0)
                {
                    CardRegistry.AddPlay(card);
                }
                CardRegistry.ForcePublish();
            }
        }
        catch (Exception e)
        {
            GD.PrintErr($"[DeckTracker] BeforeCardPlayedPostfix Error: {e.Message}");
        }
    }
    
    public static void ModifyDamagePostfix(Decimal damage, Creature? target, Creature? dealer, ValueProp props, CardModel? cardSource, CardPreviewMode previewMode, ref IEnumerable<AbstractModel> modifiers, ref Decimal __result)
    {
        if (previewMode != CardPreviewMode.None)
        {
            return;
        }

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
            decimal addAmount = mod.ModifyDamageAdditive(target, damage, props, dealer, cardSource);
            if (addAmount > 0)
            {
                snapshot.AdditiveModifiers.Add(new CardRegistry.DamageModifierSnapshot { PowerId = mod.Id.Entry, Amount = addAmount });
            }

            decimal multAmount = mod.ModifyDamageMultiplicative(target, damage, props, dealer, cardSource);
            if (multAmount != 1m && multAmount != 0m)
            {
                snapshot.MultiplicativeModifiers.Add(new CardRegistry.DamageModifierSnapshot { PowerId = mod.Id.Entry, Amount = multAmount });
                GD.Print($"[DeckTracker] ModifyDamagePostfix. Logged Multiplier: {mod.Id.Entry} with {multAmount}x");
            }
        }
        CardRegistry.CurrentAttackSnapshot.Value = snapshot;
    }
    
    public static void BeforePowerAmountChangedPostfix(ICombatState combatState, PowerModel power, Decimal amount, Creature target, Creature? applier, CardModel? cardSource)
    {
        string powerId = power.Id.Entry ?? "";
        GD.Print($"[DeckTracker] BeforePowerAmountChangedPostfix. Power: {powerId}, Amount: {amount}, Target: {target.Name}");

        if (CardRegistry.SimpleDamageTrackers.TryGetValue(powerId, out var simple))
        {
            if (amount > 0 && target.IsPlayer)
            {
                simple.LogApply(cardSource, amount, CardRegistry.GetCurrentSourceId());
            }
            return;
        }

        if (CardRegistry.TargetedTrackers.TryGetValue(powerId, out var targeted))
        {
            if (amount > 0)
            {
                targeted.LogApply(target, cardSource, amount);
            }
            return;
        }

        if (CardRegistry.ProportionalTrackers.TryGetValue(powerId, out var prop))
        {
            string tid = CardRegistry.GetCurrentSourceId(cardSource);
            if (amount > 0)
            {
                prop.AddShares(amount, tid);
            }
            else if (amount < 0)
            {
                prop.RemoveSharesProportionally(Math.Abs(amount));
            }
            return;
        }

        if (CardRegistry.QueueTrackers.TryGetValue(powerId, out var queue))
        {
            if (amount > 0)
            {
                if (powerId == "LIGHTNING_ROD_POWER" || powerId == "SPINNER_POWER")
                {
                    queue.AddDirectCharges(CardRegistry.GetCurrentSourceId(cardSource), amount);
                }
                else
                {
                    queue.LogApply(cardSource, amount);
                }
            }
            return;
        }

        if (powerId == "ROLLING_BOULDER_POWER" || powerId == "PANACHE_POWER" || powerId == "MONOLOGUE_POWER")
        {
             CardRegistry.InstancedTracker.LogInstance(power, cardSource, CardRegistry.GetCurrentSourceId());
        }

        if (CardRegistry.PersistentBuffPowerIds.Contains(powerId) && target.IsPlayer)
        {
            if (amount > 0) CardRegistry.AddPersistentBuff(powerId, amount, cardSource);
            else if (amount < 0) CardRegistry.RemovePersistentBuff(powerId, Math.Abs(amount));
            return;
        }

        if (CardRegistry.DurationDebuffPowerIds.Contains(powerId))
        {
            if (amount > 0) CardRegistry.AddDurationBuff(target, powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
            else if (amount < 0) CardRegistry.RemoveDurationBuff(target, powerId, Math.Abs(amount));
            return;
        }

        switch (power)
        {
            case ConquerorPower:
                CardRegistry.UpdateConquerorTracker(target, amount, cardSource);
                break;
            case SwordSagePower:
                CardRegistry.UpdateSovereignBladeReplayModifierTracker(amount, cardSource);
                break;
            case FurnacePower:
                CardRegistry.UpdateFurnaceHistory(amount, cardSource);
                break;
            case ReaperFormPower:
                if (amount > 0)
                {
                    CardRegistry.AddReaperFormShares(amount, cardSource);
                }
                break;
            case PoisonPower:
                if (amount > 0)
                {
                    var executingProp = CardRegistry.ProportionalTrackers.Values.FirstOrDefault(t => t.IsExecuting);
                    if (executingProp != null)
                    {
                        executingProp.DistributeProportional(amount, (id, amt) => CardRegistry.AddPoisonSharesById(target, amt, id), "Poison Handoff");
                    }
                    else
                    {
                        CardRegistry.AddPoisonSharesById(target, amount, CardRegistry.GetCurrentSourceId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemovePoisonSharesProportionally(target, Math.Abs(amount));
                }
                break;
            case CountdownPower:
                if (amount > 0)
                {
                    CardRegistry.AddCountdownHistory(amount, cardSource);
                }
                break;
            case DoomPower:
                if (amount > 0)
                {
                    if (CardRegistry.IsCountdownExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal rem = amount;
                            foreach (var c in CardRegistry.CountdownHistory)
                            {
                                if (rem <= 0)
                                {
                                    break;
                                }
                                decimal a = Math.Min(rem, c.Amount);
                                CardRegistry.AddDoomHistoryById(target, a, c.TrackingId);
                                rem -= a;
                            }
                        }
                    }
                    else if (CardRegistry.IsReaperFormExecuting.Value)
                    {
                        lock (CardRegistry.SyncRoot)
                        {
                            decimal rem = amount;
                            decimal dmg = CardRegistry.GetReaperDamage();
                            foreach (var s in CardRegistry.ReaperFormShares)
                            {
                                if (rem <= 0)
                                {
                                    break;
                                }
                                decimal a = Math.Min(rem, s.Shares * dmg);
                                CardRegistry.AddDoomHistoryById(target, a, s.TrackingId);
                                rem -= a;
                            }
                        }
                    }
                    else
                    {
                        var executingTargeted = CardRegistry.TargetedTrackers.Values.FirstOrDefault(t => t.IsExecuting);
                        if (executingTargeted != null)
                        {
                            executingTargeted.DistributeDamage(target, amount);
                        }
                        else
                        {
                            CardRegistry.AddDoomHistoryById(target, amount, CardRegistry.GetCurrentSourceId(cardSource));
                        }
                    }
                }
                break;
            case FocusPower:
                CardRegistry.LogFocusChangeById(CardRegistry.GetCurrentSourceId(cardSource), amount);
                break;
            case LoopPower:
                if (amount > 0)
                {
                    CardRegistry.AddLoop(amount, cardSource);
                }
                break;
            case ReflectPower:
                if (amount > 0 && cardSource != null)
                {
                    for (int i = 0; i < amount; i++)
                    {
                        CardRegistry.AddReflect(CardRegistry.GetTrackingId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    for (int i = 0; i > amount; i--)
                    {
                        CardRegistry.DecrementReflect();
                    }
                }
                break;
            case StrengthPower:
                if (!target.IsPlayer)
                {
                    break;
                }
                if (amount > 0)
                {
                    var handoff = CardRegistry.HandoffTrackers.Values.FirstOrDefault(t => t.IsExecuting);
                    if (handoff != null)
                    {
                        handoff.ProcessHandoff(powerId, amount);
                    }
                    else if (CardRegistry.InstancedTracker.ExecutingSourceId != null)
                    {
                        CardRegistry.AddPersistentBuffById(powerId, amount, CardRegistry.InstancedTracker.ExecutingSourceId);
                    }
                    else if (CardRegistry.IsRitualTriggering.Value)
                    {
                        foreach (var s in CardRegistry.RitualSources)
                        {
                            if (s.Value > 0)
                            {
                                CardRegistry.AddPersistentBuffById(powerId, s.Value, s.Key);
                            }
                        }
                    }
                    else
                    {
                        var executingProp = CardRegistry.ProportionalTrackers.Values.FirstOrDefault(t => t.IsExecuting);
                        if (executingProp != null)
                        {
                            executingProp.DistributeProportional(amount, (id, amt) => CardRegistry.AddPersistentBuffById(powerId, amt, id), "Persistent Handoff");
                        }
                        else
                        {
                            CardRegistry.AddPersistentBuffById(powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
                        }
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemovePersistentBuff(powerId, Math.Abs(amount));
                }
                break;
            case RitualPower:
                if (amount > 0 && target.IsPlayer)
                {
                    string sid = CardRegistry.GetCurrentSourceId(cardSource);
                    if (!string.IsNullOrEmpty(sid))
                    {
                        if (!CardRegistry.RitualSources.ContainsKey(sid))
                        {
                            CardRegistry.RitualSources[sid] = 0;
                        }
                        CardRegistry.RitualSources[sid] += amount;
                        GD.Print($"[DeckTracker] BeforePowerAmountChangedPostfix. Ritual Log: {amount} from {sid}");
                    }
                }
                break;
            case VigorPower:
                if (amount > 0)
                {
                    if (CardRegistry.HandoffTrackers["PREP_TIME_POWER"].IsExecuting)
                    {
                        CardRegistry.HandoffTrackers["PREP_TIME_POWER"].ProcessHandoff(powerId, amount);
                    }
                    else
                    {
                        CardRegistry.AddConsumableBuffById(powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemoveConsumableBuff(powerId, Math.Abs(amount));
                }
                break;
            case DoubleDamagePower:
                if (amount > 0)
                {
                    if (CardRegistry.HandoffTrackers["SHADOW_STEP_POWER"].IsExecuting)
                    {
                        CardRegistry.HandoffTrackers["SHADOW_STEP_POWER"].ProcessHandoff(powerId, amount);
                    }
                    else
                    {
                        CardRegistry.AddDurationBuff(target, powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemoveDurationBuff(target, powerId, Math.Abs(amount));
                }
                break;
            case TrackingPower:
                if (amount > 0) 
                {
                    bool isFirst = !CardRegistry.PersistentLedgers.ContainsKey(powerId) || CardRegistry.PersistentLedgers[powerId].Count == 0;
                    decimal logged = isFirst ? amount - 1 : amount;
                    if (logged > 0)
                    {
                        CardRegistry.AddPersistentBuff(powerId, logged, cardSource);
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemovePersistentBuff(powerId, Math.Abs(amount));
                }
                break;
        }
    }
    
    public static void AfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay)
    {
        CardRegistry.EndCardPlay();
        CardRegistry.ForcePublish();
        var id = cardPlay.Card.Id.Entry ?? "";
        GD.Print($"[DeckTracker] AfterCardPlayedPostfix. Card: {id}");
        if (id.Equals("SEEKING_EDGE"))
        {
            CardRegistry.UpdateSeekingEdge(cardPlay.Card);
        }
        else if (id.Equals("FAN_OF_KNIVES"))
        {
            CardRegistry.UpdateFanOfKnives(cardPlay.Card);
        }
        else if (id.Equals("SOVEREIGN_BLADE"))
        {
            CardRegistry.ProcessSovereignBladeHistory(cardPlay);
        }
        else if (id.Equals("SHIV"))
        {
            CardRegistry.ProcessShivHistory(cardPlay);
            CardRegistry.CurrentAttackSnapshot.Value = null;
        }
    }
    
    public static void AfterForgePostfix(ICombatState combatState, decimal amount, Player forger, AbstractModel? source)
    {
        GD.Print($"[DeckTracker] AfterForgePostfix. Source: {source?.Id.Entry}, Amount: {amount}");
        if (source is CardModel card)
        {
            CardRegistry.AddForge(card, amount);
        }
        else if (source is PowerModel power && power is FurnacePower)
        {
            CardRegistry.HandleFurnaceForge(amount);
        }
        else if (source is RelicModel relic)
        {
            CardRegistry.RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
            CardRegistry.AddForgeById("RELIC_" + relic.Id.Entry, amount);
        }
    }
    
    public static void PoisonAfterSideTurnStartPrefix(PoisonPower __instance)
    {
        if (!__instance.Owner.IsPlayer)
        {
            GD.Print($"[DeckTracker] PoisonAfterSideTurnStartPrefix. Target: {__instance.Owner.Name}");
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
    
    public static void DoomKillPrefix(IReadOnlyList<Creature> creatures)
    {
        GD.Print($"[DeckTracker] DoomKillPrefix. Count: {creatures.Count}");
        CardRegistry.CapturePendingDoomHp(creatures);
    }

    public static void AfterDiedToDoomPostfix(ICombatState combatState, IReadOnlyList<Creature> creatures)
    {
        GD.Print($"[DeckTracker] AfterDiedToDoomPostfix. Count: {creatures.Count}");
        CardRegistry.DistributeDoomDamage(creatures);
    }
    
    public static void CountdownAfterSideTurnStartPrefix(CountdownPower __instance)
    {
        GD.Print("[DeckTracker] CountdownAfterSideTurnStartPrefix.");
        CardRegistry.IsCountdownExecuting.Value = true;
    }

    public static void CountdownAfterSideTurnStartPostfix(CountdownPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitCountdownTaskAsync(__result);
    }

    public static void ReaperFormAfterDamageGivenPrefix(ReaperFormPower __instance, DamageResult result)
    {
        GD.Print($"[DeckTracker] ReaperFormAfterDamageGivenPrefix. Damage: {result.TotalDamage}");
        CardRegistry.StartReaperFormExecution(result.TotalDamage);
    }

    public static void ReaperFormAfterDamageGivenPostfix(ReaperFormPower __instance, ref Task __result, DamageResult result)
    {
        __result = CardRegistry.AwaitReaperFormTaskAsync(__result, result.TotalDamage);
    }

    public static void NecroMasteryAfterCurrentHpChangedPrefix(NecroMasteryPower __instance, decimal delta )
    {
        GD.Print($"[DeckTracker] NecroMasteryAfterCurrentHpChangedPrefix. Delta: {delta}");
        CardRegistry.StartNecroMasteryExecution(delta);
    }

    public static void NecroMasteryAfterCurrentHpChangedPostfix(NecroMasteryPower __instance, ref Task __result, decimal delta)
    {
        __result = CardRegistry.AwaitNecroMasteryTaskAsync(__result, delta);
    }

    public static void BeforePowerRemovedPrefix(PowerModel? power)
    {
        if (power == null)
        {
            return;
        }
        GD.Print($"[DeckTracker] BeforePowerRemovedPrefix. Power: {power.Id.Entry}");
        if (CardRegistry.SimpleDamageTrackers.TryGetValue(power.Id.Entry, out var tracker))
        {
            tracker.Reset();
        }
    }

    public static void GenericPowerPrefix(PowerModel __instance)
    {
        if (CardRegistry.SimpleDamageTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            GD.Print($"[DeckTracker] GenericPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    }

    public static void GenericPowerPostfix(PowerModel __instance, ref Task __result)
    {
        if (CardRegistry.SimpleDamageTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            __result = t.AwaitTaskAsync(__result);
        }
    }

    public static void TargetedPowerPrefix(PowerModel __instance)
    {
        if (CardRegistry.TargetedTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            GD.Print($"[DeckTracker] TargetedPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    }

    public static void TargetedPowerPostfix(PowerModel __instance, ref Task __result)
    {
        if (CardRegistry.TargetedTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            __result = t.AwaitTaskAsync(__result);
        }
    }

    public static void HandoffPowerPrefix(PowerModel __instance)
    {
        if (CardRegistry.HandoffTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            GD.Print($"[DeckTracker] HandoffPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    }

    public static void HandoffPowerPostfix(PowerModel __instance, ref Task __result)
    {
        if (CardRegistry.HandoffTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            __result = t.AwaitTaskAsync(__result);
        }
    }

    public static void ProportionalPowerPrefix(PowerModel __instance)
    {
        if (CardRegistry.ProportionalTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            GD.Print($"[DeckTracker] ProportionalPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    }

    public static void ProportionalPowerPostfix(PowerModel __instance, ref Task __result)
    {
        if (CardRegistry.ProportionalTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            __result = t.AwaitTaskAsync(__result);
        }
    }

    public static void QueuePowerPrefix(PowerModel __instance)
    {
        if (CardRegistry.QueueTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            GD.Print($"[DeckTracker] QueuePowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution(flatten: t.NeedsFlattening);
        }
    }

    public static void QueuePowerPostfix(PowerModel __instance, ref Task __result)
    {
        if (CardRegistry.QueueTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            __result = t.AwaitTaskAsync(__result, flatten: t.NeedsFlattening);
        }
    }

    public static void OrbChannelPostfix(PlayerChoiceContext choiceContext, OrbModel orb, Player player)
    {
        GD.Print($"[DeckTracker] OrbChannelPostfix. Orb: {orb.Id.Entry}");
        CardRegistry.RegisterChanneledOrb(orb, CardRegistry.CurrentPlayingCard);
    }

    public static void OrbPassivePrefix(OrbModel __instance)
    {
        string? forcingActor = null;
        if (CardRegistry.IsLoopExecuting.Value && CardRegistry.CurrentTurnLoopQueue.Count > 0)
        {
            forcingActor = CardRegistry.CurrentTurnLoopQueue[0];
            CardRegistry.CurrentTurnLoopQueue.RemoveAt(0);
        }
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            forcingActor = "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
        }
        else if (CardRegistry.CurrentPlayingCard != null)
        {
            forcingActor = CardRegistry.GetTrackingId(CardRegistry.CurrentPlayingCard);
        }
        else
        {
            lock (CardRegistry.SyncRoot)
            {
                int count = CardRegistry.EotPassiveCounts.GetValueOrDefault(__instance, 0) + 1;
                CardRegistry.EotPassiveCounts[__instance] = count;
                if (count > 1)
                {
                    forcingActor = "RELIC_GoldPlatedCables";
                }
            }
        }
        GD.Print($"[DeckTracker] OrbPassivePrefix. Orb: {__instance.Id.Entry}, ForcingActor: {forcingActor}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, false, __instance.PassiveVal, forcingActor);
    }
    
    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        GD.Print($"[DeckTracker] OrbPassivePostfix. Orb: {__instance.Id.Entry}");
        __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
    }

    public static void OrbEvokePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] OrbEvokePrefix. Orb: {__instance.Id.Entry}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, true, __instance.EvokeVal);
    }
    
    public static void OrbEvokePostfix(OrbModel __instance, ref Task<IEnumerable<Creature>> __result)
    {
        GD.Print($"[DeckTracker] OrbEvokePostfix. Orb: {__instance.Id.Entry}");
        __result = CardRegistry.AwaitOrbEvokeTaskAsync(__result, __instance);
    }
    
    public static void TempFocusApplyPrefix(TemporaryFocusPower __instance)
    {
        GD.Print("[DeckTracker] TempFocusApplyPrefix.");
        CardRegistry.IsApplyingTemporaryFocus.Value = true;
    }

    public static void TempFocusApplyPostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusApplyAsync(__result);
    }

    public static void TempFocusExpirePrefix(TemporaryFocusPower __instance)
    {
        GD.Print("[DeckTracker] TempFocusExpirePrefix.");
        CardRegistry.IsExpiringTemporaryFocus.Value = true;
    }

    public static void TempFocusExpirePostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusExpireAsync(__result);
    }

    public static void LoopPrefix(LoopPower __instance)
    {
        GD.Print("[DeckTracker] LoopPrefix.");
        CardRegistry.IsLoopExecuting.Value = true;
        CardRegistry.CurrentTurnLoopQueue.Clear();
        lock (CardRegistry.SyncRoot)
        {
            foreach (var contribution in CardRegistry.LoopHistory)
            {
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
        GD.Print("[DeckTracker] RollingBoulderAfterPlayerTurnStartPrefix.");
        CardRegistry.InstancedTracker.StartExecution(__instance);
    }

    public static void RollingBoulderAfterPlayerTurnStartPostfix(RollingBoulderPower __instance, ref Task __result)
    {
        __result = CardRegistry.InstancedTracker.AwaitTaskAsync(__result, __instance);
    }
    
    public static void PrepTimePrefix(PrepTimePower __instance)
    {
        GD.Print("[DeckTracker] PrepTimePrefix.");
        CardRegistry.HandoffTrackers["PREP_TIME_POWER"].StartExecution();
    }

    public static void PrepTimePostfix(PrepTimePower __instance, ref Task __result)
    {
        __result = CardRegistry.HandoffTrackers["PREP_TIME_POWER"].AwaitTaskAsync(__result);
    }

    public static void RelicAfterObtainedPrefix(RelicModel __instance)
    {
        CardRegistry.RelicNameCache[__instance.Id.Entry] = __instance.Title.GetFormattedText();
        var stats = CardRegistry.GetOrCreateRelicStats(__instance.Id.Entry);
        stats.FloorAdded = __instance.FloorAddedToDeck;
        stats.IsActive = true;
        GD.Print($"[DeckTracker] RelicAfterObtainedPrefix. Relic: {__instance.Id.Entry}, Floor: {stats.FloorAdded}");
    }

    public static void PlayerRemoveRelicPostfix(Player __instance, RelicModel relic)
    {
        if (relic != null)
        {
            GD.Print($"[DeckTracker] PlayerRemoveRelicPostfix. Relic: {relic.Id.Entry}");
            CardRegistry.HandleRelicRemove(relic, ExtractFloorNum(__instance.RunState));
        }
    }

    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (target.IsPlayer)
        {
            return;
        }
        var damageAmount = results.TotalDamage;
        GD.Print($"[DeckTracker] AfterDamageGivenPostfix. Damage: {damageAmount}, Target: {target.Name}, Source: {cardSource?.Id.Entry}");

        if (CardRegistry.PendingBootDamage.Value > 0)
        {
            damageAmount -= CardRegistry.PendingBootDamage.Value;
            CardRegistry.PendingBootDamage.Value = 0;
            GD.Print($"[DeckTracker]   -> Reduced by Boot: {damageAmount}");
        }

        if (cardSource == null && !string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            CardRegistry.AddRelicDamage(RelicExecutionManager.ExecutingRelicId.Value, damageAmount);
            return;
        }

        if (CardRegistry.CurrentPoisonTarget.Value == target && damageAmount > 0)
        {
            CardRegistry.DistributePoisonDamage(target, damageAmount);
            if (!target.IsAlive)
            {
                CardRegistry.ClearStateForTarget(target);
            }
            return;
        }

        if (CardRegistry.ExecutingOrb != null && damageAmount > 0)
        {
            CardRegistry.DistributeOrbDamage(CardRegistry.ExecutingOrb, damageAmount, CardRegistry.ExecutingOrb.Orb.Owner.Creature);
            return;
        }

        // Generic check for TargetedTrackers (handles Strangle/Oblivion)
        var executingTargeted = CardRegistry.TargetedTrackers.Values.FirstOrDefault(t => t.IsExecuting);
        if (executingTargeted != null && damageAmount > 0)
        {
            executingTargeted.DistributeDamage(target, damageAmount);
            return;
        }

        var simple = CardRegistry.SimpleDamageTrackers.Values.FirstOrDefault(t => t.IsExecuting);
        if (simple != null && damageAmount > 0)
        {
            simple.DistributeDamage(damageAmount);
            return;
        }

        var prop = CardRegistry.ProportionalTrackers.Values.FirstOrDefault(t => t.IsExecuting);
        if (prop != null && damageAmount > 0)
        {
            prop.DistributeDamage(damageAmount);
            return;
        }

        if (CardRegistry.IsNecroMasteryExecuting && damageAmount > 0)
        {
            CardRegistry.DistributeNecroMasteryDamage(damageAmount);
            return;
        }

        if (CardRegistry.IsReflectExecuting && damageAmount > 0)
        {
            CardRegistry.DistributeReflectDamage(damageAmount);
            return;
        }

        if (cardSource == null)
        {
            if (CardRegistry.InstancedTracker.ExecutingSourceId != null)
            {
                CardRegistry.AddDamageById(CardRegistry.InstancedTracker.ExecutingSourceId, damageAmount);
                return;
            }
            var activePot = CardRegistry.CurrentPlayingPotion;
            if (activePot != null && target != null && !target.IsPlayer && CardRegistry.PotionInstanceIds.TryGetValue(activePot, out var pid))
            {
                CardRegistry.AddDamageById(pid, damageAmount);
                return;
            }
            return;
        }

        decimal baseDmg = damageAmount;
        if (CardRegistry.CurrentAttackSnapshot.Value != null && CardRegistry.CurrentAttackSnapshot.Value.CardSource == cardSource)
        {
            baseDmg = CardRegistry.ProcessDamageSnapshot(CardRegistry.CurrentAttackSnapshot.Value, damageAmount);
        }

        if (!CardRegistry.TryHandleCustomCardDamage(combatState, dealer, results, target, cardSource, baseDmg))
        {
            CardRegistry.AddDamage(cardSource, baseDmg);
        }
    }
    
    public static void AfterPotionProcuredPrefix(PotionModel potion)
    {
        int floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] AfterPotionProcuredPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.RegisterPotionProcured(potion, floor);
    }

    public static void AfterPotionDiscardedPrefix(PotionModel potion)
    {
        int floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] AfterPotionDiscardedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.MarkPotionDiscarded(potion, floor);
    }

    public static void BeforePotionUsedPrefix(PotionModel potion)
    {
        int floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] BeforePotionUsedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.MarkPotionUsed(potion, floor);
        CardRegistry.SetPlayingPotion(potion);
    }

    public static void AfterPotionUsedPrefix(PotionModel potion)
    {
        GD.Print($"[DeckTracker] AfterPotionUsedPrefix. Potion: {potion.Id.Entry}");
        CardRegistry.SetPlayingPotion(null);
    }

    public static void RitualPowerTurnEndPrefix()
    {
        GD.Print("[DeckTracker] RitualPowerTurnEndPrefix.");
        CardRegistry.IsRitualTriggering.Value = true;
    }

    public static void RitualPowerTurnEndPostfix()
    {
        CardRegistry.IsRitualTriggering.Value = false;
    }

    public static void HandDrillAfterDamagePrefix()
    {
        RelicExecutionManager.ExecutingRelicId.Value = "HAND_DRILL";
    }

    public static void HandDrillAfterDamagePostfix()
    {
        RelicExecutionManager.ExecutingRelicId.Value = null;
    }
    
    public static void TheBootModifyHpPostfix(MegaCrit.Sts2.Core.Models.Relics.TheBoot __instance, decimal amount, ref decimal __result)
    {
        if (__result > amount)
        {
            var floor = (int)Math.Floor(amount);
            var boot = (int)Math.Floor(__result - floor);
            GD.Print($"[DeckTracker] TheBootModifyHpPostfix. Damage: {boot}");
            CardRegistry.AddDamageById("RELIC_" + (__instance.Id.Entry ?? "THE_BOOT"), boot);
            CardRegistry.PendingBootDamage.Value += boot;
        }
    }
    
    private static int ExtractFloorNum(IRunState? runState) => runState?.TotalFloor ?? 1;
    private static int ExtractActNum(IRunState? runState) => (runState?.CurrentActIndex ?? 0) + 1;
    private static string GetCombatType(IRunState? runState)
    {
        if (runState != null)
        {
            try
            {
                var t = runState.BaseRoom?.RoomType;
                if (t == RoomType.Monster) return "Hallway";
                if (t == RoomType.Elite) return "Elite";
                if (t == RoomType.Boss) return "Boss";
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
            var r = runState.GetType().GetProperty("Rng")?.GetValue(runState);
            return r?.GetType().GetProperty("StringSeed")?.GetValue(r)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
    private static List<string> ScanDeckForCards(IRunState? runState)
    {
        List<string> ids = new();
        if (runState != null)
        {
            foreach (var p in runState.Players)
            {
                foreach (var c in p.Deck.Cards)
                {
                    CardRegistry.RegisterCard(c);
                    ids.Add(CardRegistry.GetTrackingId(c));
                }
            }
        }
        return ids;
    }
    public static void RunManagerCleanUpPrefix() => CardRegistry.ClearSession();
}