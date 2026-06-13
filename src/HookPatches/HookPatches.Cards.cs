using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void BeforeCardRemovedPostfix(IRunState runState, CardModel card) => Guard(nameof(BeforeCardRemovedPostfix), () =>
    {
        var currentFloor = ExtractFloorNum(runState);
        GD.Print($"[DeckTracker] BeforeCardRemovedPostfix. Card: {card.Id.Entry}, Floor: {currentFloor}");
        CardRegistry.HandleRemove(card, currentFloor);
    });

    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy) => Guard(nameof(AfterCardChangedPilesPostfix), () =>
    {
        if (combatState == null)
        {
            return;
        }
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
    });

    public static void AfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw) => Guard(nameof(AfterCardDrawnPostfix), () =>
    {
        GD.Print($"[DeckTracker] AfterCardDrawnPostfix. Card: {card.Id.Entry}, FromHand: {fromHandDraw}");
        CardRegistry.RegisterCard(card);
        CardRegistry.AddDraw(card);
    });

    public static void BeforeCardPlayedPostfix(ICombatState combatState, CardPlay cardPlay) => Guard(nameof(BeforeCardPlayedPostfix), () =>
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
    });

    // Has ref parameters, which cannot be captured by the Guard lambda, so it is isolated inline instead.
    public static void ModifyDamagePostfix(Decimal damage, Creature? target, Creature? dealer, ValueProp props, CardModel? cardSource, CardPreviewMode previewMode, ref IEnumerable<AbstractModel> modifiers, ref Decimal __result)
    {
        try
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
                var addAmount = mod.ModifyDamageAdditive(target, damage, props, dealer, cardSource);
                if (addAmount > 0)
                {
                    snapshot.AdditiveModifiers.Add(new CardRegistry.DamageModifierSnapshot { PowerId = mod.Id.Entry, Amount = addAmount });
                }

                var multAmount = mod.ModifyDamageMultiplicative(target, damage, props, dealer, cardSource);
                if (multAmount != 1m && multAmount != 0m)
                {
                    snapshot.MultiplicativeModifiers.Add(new CardRegistry.DamageModifierSnapshot { PowerId = mod.Id.Entry, Amount = multAmount });
                    GD.Print($"[DeckTracker] ModifyDamagePostfix. Logged Multiplier: {mod.Id.Entry} with {multAmount}x");
                }
            }
            CardRegistry.CurrentAttackSnapshot.Value = snapshot;
        }
        catch (Exception e)
        {
            LogHookError(nameof(ModifyDamagePostfix), e);
        }
    }

    public static void AfterCardPlayedPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardPlay cardPlay) => Guard(nameof(AfterCardPlayedPostfix), () =>
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
    });

    public static void AfterForgePostfix(ICombatState combatState, decimal amount, Player forger, AbstractModel? source) => Guard(nameof(AfterForgePostfix), () =>
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
        else if (source is PotionModel potion)
        {
            CardRegistry.AddPotionForge(potion, amount);
        }
        else
        {
            GD.Print($"[DeckTracker] Unknown forge source: {source?.Id.Entry}");
        }
    });

    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource) => Guard(nameof(AfterDamageGivenPostfix), () =>
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
    });
}
