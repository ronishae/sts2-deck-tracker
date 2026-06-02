using System;
using System.Linq;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    

    // Tracks exactly who applied doom, in exact order
    private static readonly Dictionary<Creature, List<DoomContribution>> DoomHistory = new();
    
    // Captures HP right before CreatureCmd.Kill happens
    private static readonly Dictionary<Creature, decimal> PendingDoomHp = new();

    public static void RouteDoomApplication(Creature target, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;

        if (IsCountdownExecuting.Value)
        {
            lock (SyncRoot)
            {
                decimal rem = amount;
                foreach (var c in CountdownHistory)
                {
                    if (rem <= 0) break;
                    decimal a = Math.Min(rem, c.Amount);
                    AddDoomHistoryById(target, a, c.TrackingId);
                    rem -= a;
                }
            }
            return;
        }

        if (IsReaperFormExecuting.Value)
        {
            lock (SyncRoot)
            {
                decimal rem = amount;
                decimal dmg = GetReaperDamage();
                foreach (var s in ReaperFormShares)
                {
                    if (rem <= 0) break;
                    decimal a = Math.Min(rem, s.Shares * dmg);
                    AddDoomHistoryById(target, a, s.TrackingId);
                    rem -= a;
                }
            }
            return;
        }

        var executingTargeted = TargetedTrackers.Values.FirstOrDefault(t => t.IsExecuting);
        if (executingTargeted != null)
        {
            executingTargeted.DistributeDamage(target, amount);
        }
        else
        {
            AddDoomHistoryById(target, amount, GetCurrentSourceId(cardSource));
        }
    }

    public static void ResetDoomState()
    {
        lock (SyncRoot)
        {
            DoomHistory.Clear();
            PendingDoomHp.Clear();
        }
    }

    public static void AddDoomHistory(Creature target, decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            if (!DoomHistory.ContainsKey(target)) DoomHistory[target] = new List<DoomContribution>();

            string uniqueId = GetTrackingId(cardSource);
            DoomHistory[target].Add(new DoomContribution { TrackingId = uniqueId, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} Doom to FIFO queue for {uniqueId}.");
        }
        Publish();
    }
    
    public static void AddDoomHistoryById(Creature target, decimal amount, string uniqueId)
    {
        if (amount <= 0 || string.IsNullOrEmpty(uniqueId)) return;

        lock (SyncRoot)
        {
            if (!DoomHistory.ContainsKey(target)) DoomHistory[target] = new List<DoomContribution>();

            DoomHistory[target].Add(new DoomContribution { TrackingId = uniqueId, Amount = amount });
            GD.Print($"[DeckTracker] Chained {amount} Doom to FIFO queue for {uniqueId}.");
        }
        Publish();
    }

    // Runs in the Prefix to grab the HP before it becomes 0
    public static void CapturePendingDoomHp(IReadOnlyList<Creature> creatures)
    {
        lock (SyncRoot)
        {
            foreach (var creature in creatures)
            {
                if (creature.CurrentHp > 0)
                {
                    PendingDoomHp[creature] = creature.CurrentHp;
                    GD.Print($"[DeckTracker] Captured {creature.CurrentHp} HP for {creature.Name} before Doom execution.");
                }
            }
        }
    }

    // Runs in the AfterDiedToDoom hook to pay out the damage
    public static void DistributeDoomDamage(IReadOnlyList<Creature> creatures)
    {
        lock (SyncRoot)
        {
            foreach (var creature in creatures)
            {
                if (!PendingDoomHp.TryGetValue(creature, out decimal hpToDistribute) || hpToDistribute <= 0)
                {
                    continue;
                }
                PendingDoomHp.Remove(creature);
                
                if (!DoomHistory.TryGetValue(creature, out var history) || history.Count == 0)
                {
                    continue;
                }

                decimal remainingHp = hpToDistribute;
                
                foreach (var contribution in history)
                {
                    if (remainingHp <= 0) break;

                    decimal amountToAttribute = Math.Min(remainingHp, contribution.Amount);
                    
                    GD.Print($"[DeckTracker] Attributing {amountToAttribute} Doom damage to {contribution.TrackingId}");
                    
                    AddDamageById(contribution.TrackingId, amountToAttribute);
                    
                    remainingHp -= amountToAttribute;
                }

                if (remainingHp > 0)
                {
                    GD.Print($"[DeckTracker] Warning: {remainingHp} Doom damage unaccounted for by card history.");
                }

                DoomHistory.Remove(creature);
            }
        }
        Publish();
    }
}