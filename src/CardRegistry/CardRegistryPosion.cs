using Godot;
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

    public static void ResetPoisonState()
    {
        lock (SyncRoot)
        {
            PoisonShares.Clear();
        }
    }
    
    public static void AddPoisonSharesById(Creature target, decimal amount, string? uniqueId)
    {
        GD.Print($"[DeckTracker] AddPoisonShares called with cardSource {uniqueId}");
        if (amount <= 0 || string.IsNullOrEmpty(uniqueId)) return;
        GD.Print($"[DeckTracker] AddPoisonShares null and amount check passed -- poison applied by card!");
        
        lock (SyncRoot)
        {
            if (!PoisonShares.ContainsKey(target)) PoisonShares[target] = new List<PoisonContribution>();
            
            var existing = PoisonShares[target].FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Shares += amount;
            } 
            else 
            {
                PoisonShares[target].Add(new PoisonContribution { TrackingId = uniqueId, Shares = amount });
            }
            GD.Print($"[DeckTracker] Chained {amount} Poison shares to Fumes card {uniqueId}.");
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
        if (decreaseAmount <= 0) return;

        lock (SyncRoot)
        {
            if (!PoisonShares.TryGetValue(target, out var shares) || shares.Count == 0) return;

            decimal totalShares = shares.Sum(c => c.Shares);
            if (totalShares <= 0) return;

            // Safety check: If a monster is cleansed of all poison, just wipe the bucket
            if (decreaseAmount >= totalShares)
            {
                GD.Print($"[DeckTracker] Poison cleansed/wiped from {target.Name}. Clearing shares.");
                PoisonShares.Remove(target);
                return;
            }

            // Proportionally reduce each share
            foreach (var share in shares)
            {
                decimal percentage = share.Shares / totalShares;
                decimal amountToShave = decreaseAmount * percentage;
                
                share.Shares -= amountToShave;
                GD.Print($"[DeckTracker] Decayed {share.TrackingId} shares by {amountToShave:F2}. Remaining: {share.Shares:F2}");
            }

            // Clean up any microscopic floating-point leftovers
            shares.RemoveAll(c => c.Shares <= 0.01m);
            if (shares.Count == 0) PoisonShares.Remove(target);
        }
    }
    
    public static void DistributePoisonDamage(Creature target, decimal totalDamage)
    {
        lock (SyncRoot)
        {
            if (!PoisonShares.TryGetValue(target, out var shares) || shares.Count == 0) 
                return;

            var totalShares = shares.Sum(c => c.Shares);
            if (totalShares <= 0) return;

            foreach (var share in shares)
            {
                var percentage = share.Shares / totalShares;
                var attributedDamage = totalDamage * percentage;

                GD.Print($"[DeckTracker] Attributing {attributedDamage} Poison damage to {share.TrackingId}");

                AddDamageById(share.TrackingId, attributedDamage);
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
            GD.Print("[DeckTracker] Poison task completed and context cleared.");
        }
    }
}