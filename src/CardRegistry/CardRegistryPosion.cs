using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public class PoisonContribution
    {
        public string TrackingId { get; init; } = "";
        public decimal Shares { get; set; }
    }

    private static readonly Dictionary<Creature, List<PoisonContribution>> PoisonShares = new();
    
    // AsyncLocal is used to keep the variable consistent across threads when dealing with async tasks
    public static readonly AsyncLocal<Creature?> CurrentPoisonTarget = new();

    public static void RoutePoisonApplication(Creature target, decimal amount, CardModel? cardSource)
    {
        if (amount > 0)
        {
            var executingProp = ProportionalTrackers.Values.FirstOrDefault(t => t.IsExecuting);
            if (executingProp != null)
            {
                executingProp.DistributeProportional(amount, (id, amt) => AddPoisonSharesById(target, amt, id), "Poison Handoff");
            }
            else
            {
                AddPoisonSharesById(target, amount, GetCurrentSourceId(cardSource));
            }
        }
        else if (amount < 0)
        {
            RemovePoisonSharesProportionally(target, Math.Abs(amount));
        }
    }

    public static void ResetPoisonState()
    {
        lock (SyncRoot)
        {
            PoisonShares.Clear();
            GD.Print("[DeckTracker] ResetPoisonState. Poison shares cleared.");
        }
    }
    
    public static void AddPoisonSharesById(Creature target, decimal amount, string? uniqueId)
    {
        if (amount <= 0 || string.IsNullOrEmpty(uniqueId))
        {
            return;
        }
        
        lock (SyncRoot)
        {
            if (!PoisonShares.ContainsKey(target))
            {
                PoisonShares[target] = new List<PoisonContribution>();
            }
            
            var existing = PoisonShares[target].FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Shares += amount;
            } 
            else 
            {
                PoisonShares[target].Add(new PoisonContribution { TrackingId = uniqueId, Shares = amount });
            }
            GD.Print($"[DeckTracker] AddPoisonSharesById. Added {amount} shares to {uniqueId} for {target.Name}");
        }
    }
    
    // Called by BeforePowerAmountChanged when Poison goes UP
    public static void AddPoisonShares(Creature target, decimal amount, CardModel? cardSource)
    {
        AddPoisonSharesById(target, amount, GetTrackingId(cardSource));
    }
    
    // Decays the shares so old cards don't leech damage from fresh applications
    public static void RemovePoisonSharesProportionally(Creature target, decimal decreaseAmount)
    {
        if (decreaseAmount <= 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (!PoisonShares.TryGetValue(target, out var shares) || shares.Count == 0)
            {
                return;
            }

            decimal totalShares = shares.Sum(c => c.Shares);
            if (totalShares <= 0)
            {
                return;
            }

            // Safety check: If a monster is cleansed of all poison, just wipe the bucket
            if (decreaseAmount >= totalShares)
            {
                GD.Print($"[DeckTracker] RemovePoisonSharesProportionally. Poison wiped from {target.Name}.");
                PoisonShares.Remove(target);
                return;
            }

            GD.Print($"[DeckTracker] RemovePoisonSharesProportionally. Removing {decreaseAmount} from {totalShares} total shares on {target.Name}");
            foreach (var share in shares)
            {
                decimal percentage = share.Shares / totalShares;
                decimal amountToShave = decreaseAmount * percentage;
                
                share.Shares -= amountToShave;
                GD.Print($"[DeckTracker]   -> Decayed {share.TrackingId} by {amountToShave:F2}");
            }

            shares.RemoveAll(c => c.Shares <= 0.01m);
            if (shares.Count == 0)
            {
                PoisonShares.Remove(target);
            }
        }
    }
    
    public static void DistributePoisonDamage(Creature target, decimal totalDamage)
    {
        lock (SyncRoot)
        {
            if (!PoisonShares.TryGetValue(target, out var shares) || shares.Count == 0) 
            {
                return;
            }

            var totalShares = shares.Sum(c => c.Shares);
            if (totalShares <= 0)
            {
                return;
            }

            GD.Print($"[DeckTracker] DistributePoisonDamage. Target: {target.Name}, Total: {totalDamage}");
            foreach (var share in shares)
            {
                var percentage = share.Shares / totalShares;
                var attributedDamage = totalDamage * percentage;

                AddDamageById(share.TrackingId, attributedDamage);
                GD.Print($"[DeckTracker]   -> Attributed {attributedDamage:F2} to {share.TrackingId}");
            }
        }
        Publish();
    }
    
    public static async Task AwaitPoisonTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            CurrentPoisonTarget.Value = null;
            GD.Print("[DeckTracker] AwaitPoisonTaskAsync finished.");
        }
    }
}