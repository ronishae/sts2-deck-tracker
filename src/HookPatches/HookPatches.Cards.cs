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
        Log.Debug($"BeforeCardRemovedPostfix. Card: {card.Id.Entry}, Floor: {currentFloor}");
        CardRegistry.HandleRemove(card, currentFloor);
    });

    public static void AfterCardChangedPilesPostfix(IRunState runState, ICombatState? combatState, CardModel card, PileType oldPile, AbstractModel? clonedBy) => Guard(nameof(AfterCardChangedPilesPostfix), () =>
    {
        if (combatState == null)
        {
            return;
        }
        // Capture the creator the instant a generated card is placed into any pile (including the discard
        // pile when the hand is full), while the generating source is still executing. Registration may
        // not happen until the card is later drawn, by which point the context is gone.
        CardRegistry.TagGeneratedCardOnCreation(card);
        if (card.Pile != null && card.Pile.Type == PileType.Hand && oldPile != PileType.Draw)
        {
            if (CardRegistry.IsDeferringDraws())
            {
                Log.Debug($"AfterCardChangedPilesPostfix. Deferring draw for {card.Id.Entry}");
                CardRegistry.DeferDraw(card);
            }
            else
            {
                Log.Debug($"AfterCardChangedPilesPostfix. Direct draw for {card.Id.Entry}");
                CardRegistry.RegisterCard(card);
                CardRegistry.AddDraw(card);
                CardRegistry.ForcePublish();
            }
        }
    });

    public static void AfterCardDrawnPostfix(ICombatState combatState, PlayerChoiceContext choiceContext, CardModel card, bool fromHandDraw) => Guard(nameof(AfterCardDrawnPostfix), () =>
    {
        Log.Debug($"AfterCardDrawnPostfix. Card: {card.Id.Entry}, FromHand: {fromHandDraw}");
        CardRegistry.RegisterCard(card);
        CardRegistry.AddDraw(card);
    });

    public static void BeforeCardPlayedPostfix(ICombatState combatState, CardPlay cardPlay) => Guard(nameof(BeforeCardPlayedPostfix), () =>
    {
        var cardProp = cardPlay.GetType().GetProperty("Card");
        if (cardProp?.GetValue(cardPlay) is CardModel card)
        {
            Log.Debug($"BeforeCardPlayedPostfix. Card: {card.Id.Entry}, PlayIndex: {cardPlay.PlayIndex}");
            CardRegistry.StartCardPlay(card);
            CardRegistry.RegisterCard(card);
            if (cardPlay.PlayIndex == 0)
            {
                CardRegistry.AddPlay(card);
            }
            CardRegistry.ForcePublish();
        }
    });

    // A card autoplayed without ever entering the hand (Uproar's draw-pile attack, Cascade/Mayhem via
    // AutoPlayFromDrawPile which pre-moves cards to the Play pile, or a generated card played directly)
    // never triggers a draw hook, yet its play is counted in BeforeCardPlayedPostfix — so play rate
    // could exceed 100%. Credit an implicit draw here. Cards still in hand (Stampede, Hellraiser) and
    // Sly discards (autoplayed from the discard pile, type SlyDiscard) were already drawn, so skip them
    // to avoid double-counting. This hook fires only after AutoPlay's bail-outs, so the card truly plays.
    public static void BeforeCardAutoPlayedPostfix(CardModel card, AutoPlayType type) => Guard(nameof(BeforeCardAutoPlayedPostfix), () =>
    {
        var pileType = card.Pile?.Type;
        if (type == AutoPlayType.SlyDiscard || pileType == PileType.Hand)
        {
            Log.Debug($"BeforeCardAutoPlayedPostfix. Skip draw (already drawn). Card: {card.Id.Entry}, Type: {type}, Pile: {pileType}");
            return;
        }
        Log.Debug($"BeforeCardAutoPlayedPostfix. Implicit draw. Card: {card.Id.Entry}, Type: {type}, Pile: {pileType}");
        CardRegistry.RegisterAndAddDraw(card);
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

            // ModifyDamage also fires for sourceless damage: enemy attack-intent recalcs (which
            // happen on every hover) and poison ticks. These pass previewMode None so the check
            // above misses them, and a snapshot with no card source can never be consumed in
            // AfterDamageGivenPostfix. Skip them to avoid spamming logs and building dead snapshots.
            if (cardSource == null)
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
                    Log.Debug($"ModifyDamagePostfix. Logged Multiplier: {mod.Id.Entry} with {multAmount}x");
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
        Log.Debug($"AfterCardPlayedPostfix. Card: {id}");
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
        Log.Debug($"AfterForgePostfix. Source: {source?.Id.Entry}, Amount: {amount}");
        if (source is CardModel card)
        {
            CardRegistry.AddForge(card, amount);
        }
        else if (source is FurnacePower)
        {
            CardRegistry.HandleFurnaceForge(amount);
        }
        else if (source is HammerTimePower hammerTimePower)
        {
            CardRegistry.HandleHammerTimeForge(hammerTimePower, amount);
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
            Log.Warn($"Unknown forge source: {source?.Id.Entry}");
        }
    });

    public static void LoseBlockPrefix(Creature creature, decimal amount) => Guard(nameof(LoseBlockPrefix), () =>
    {
        CardRegistry.HandleForcedBlockLoss(creature, amount);
    });

    public static void AfterDamageGivenPostfix(PlayerChoiceContext? choiceContext, ICombatState combatState, Creature? dealer, DamageResult results, ValueProp props, Creature target, CardModel? cardSource) => Guard(nameof(AfterDamageGivenPostfix), () =>
    {
        if (target.IsPlayer)
        {
            return;
        }
        var damageAmount = results.TotalDamage;
        Log.Debug($"AfterDamageGivenPostfix. Damage: {damageAmount}, Target: {target.Name}, Source: {cardSource?.Id.Entry}");

        if (CardRegistry.PendingBootDamage.Value > 0)
        {
            damageAmount -= CardRegistry.PendingBootDamage.Value;
            CardRegistry.PendingBootDamage.Value = 0;
            Log.VeryDebug($"  -> Reduced by Boot: {damageAmount}");
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
            var activePotId = CardRegistry.CurrentPlayingPotionId;
            if (!string.IsNullOrEmpty(activePotId) && target != null && !target.IsPlayer)
            {
                CardRegistry.AddDamageById(activePotId, damageAmount);
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
