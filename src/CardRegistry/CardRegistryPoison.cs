using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly Dictionary<Creature, List<Contribution>> PoisonShares = new();

    // Plain field (not AsyncLocal): turn-start poison damage is dealt through the game's action
    // queue, which an AsyncLocal set in our prefix would not flow into. Combat is single-threaded
    // and sequential, so a plain field stays visible to that queue-dispatched damage.
    public static Creature? CurrentPoisonTarget;

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
            Log.Debug("ResetPoisonState. Poison shares cleared.");
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
                PoisonShares[target] = new List<Contribution>();
            }
            
            var existing = PoisonShares[target].FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Amount += amount;
            } 
            else 
            {
                PoisonShares[target].Add(new Contribution { TrackingId = uniqueId, Amount = amount });
            }
            Log.Debug($"AddPoisonSharesById. Added {amount} shares to {uniqueId} for {target.Name}");
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

            var totalShares = shares.Sum(c => c.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            // Safety check: If a monster is cleansed of all poison, just wipe the bucket
            if (decreaseAmount >= totalShares)
            {
                Log.Debug($"RemovePoisonSharesProportionally. Poison wiped from {target.Name}.");
                PoisonShares.Remove(target);
                return;
            }

            Log.Debug($"RemovePoisonSharesProportionally. Removing {decreaseAmount} from {totalShares} total shares on {target.Name}");
            foreach (var share in shares)
            {
                var percentage = share.Amount / totalShares;
                var amountToShave = decreaseAmount * percentage;
                
                share.Amount -= amountToShave;
                Log.VeryDebug($"  -> Decayed {share.TrackingId} by {amountToShave:F2}");
            }

            shares.RemoveAll(c => c.Amount <= 0.01m);
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

            var totalShares = shares.Sum(c => c.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            Log.Debug($"DistributePoisonDamage. Target: {target.Name}, Total: {totalDamage}");
            foreach (var share in shares)
            {
                var percentage = share.Amount / totalShares;
                var attributedDamage = totalDamage * percentage;

                AddDamageById(share.TrackingId, attributedDamage);
                Log.VeryDebug($"  -> Attributed {attributedDamage:F2} to {share.TrackingId}");
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
            CurrentPoisonTarget = null;
            Log.VeryDebug("AwaitPoisonTaskAsync finished.");
        }
    }
}