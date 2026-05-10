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

    public static void ClearStateForTarget(Creature target)
    {
        lock (SyncRoot)
        {
            PoisonShares.Remove(target);
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