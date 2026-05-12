using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly Dictionary<Creature, List<StrangleContribution>> StrangleLedgers = new();
    public static readonly AsyncLocal<bool> IsStrangleExecuting = new();

    public static void ResetStrangleState()
    {
        lock (SyncRoot)
        {
            StrangleLedgers.Clear();
        }
    }

    public static void LogStrangleApply(Creature target, CardModel? cardSource, int amount)
    {
        if (amount <= 0 || target == null) return;

        lock (SyncRoot)
        {
            if (!StrangleLedgers.TryGetValue(target, out var ledger))
            {
                ledger = new List<StrangleContribution>();
                StrangleLedgers[target] = ledger;
            }

            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            ledger.Add(new StrangleContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogStrangleApply. Target: {target.Name}, Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void ClearStrangle(Creature target)
    {
        lock (SyncRoot)
        {
            if (StrangleLedgers.Remove(target))
            {
                GD.Print($"[DeckTracker] ClearStrangle. Target: {target.Name}");
            }
        }
    }

    public static void DistributeStrangleDamage(Creature target, decimal totalDamage)
    {
        if (totalDamage <= 0 || target == null) return;

        lock (SyncRoot)
        {
            if (!StrangleLedgers.TryGetValue(target, out var ledger))
            {
                GD.Print($"[DeckTracker] DistributeStrangleDamage. No ledger found for {target.Name}");
                return;
            }

            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeStrangleDamage. Target: {target.Name}, Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < ledger.Count && remainingDamage > 0; i++)
            {
                var contribution = ledger[i];
                // In Strangle, every card play deals damage equal to the FULL stack.
                // However, the user wants FIFO attribution for overkill.
                // This means we attribute up to the contribution amount of each card.
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeStrangleDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeStrangleDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitStrangleTaskAsync(Task originalTask, Creature target, decimal expectedDamage)
    {
        try
        {
            IsStrangleExecuting.Value = true;
            await originalTask;
        }
        finally
        {
            IsStrangleExecuting.Value = false;
            // The damage distribution is handled by the AfterDamageGiven hook if we set the flag,
            // OR we can do it here if we know the damage. 
            // StranglePower.AfterCardPlayed deals damage via CreatureCmd.Damage.
            // Since we wrap the task, we can't easily get the DamageResult here unless we hook AfterDamageGiven.
        }
    }
}
