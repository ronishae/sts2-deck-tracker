using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- DEMISE POWER TRACKING ---
    public class DemiseShare
    {
        public string TrackingId { get; set; } = "";
        public decimal Shares { get; set; }
    }
    
    public static Dictionary<Creature, List<DemiseShare>> DemiseLedgers = new();
    public static readonly AsyncLocal<bool> IsDemiseExecuting = new();
    
    // TODO: Implemented using shares, but demise never decreases and 1 demise = 1 damage
    // so it is mostly over-engineered
    // Can be simplified likely...
    public static void AddDemiseSharesById(Creature target, decimal amount, string trackingId)
    {
        lock (SyncRoot)
        {
            if (!DemiseLedgers.ContainsKey(target))
                DemiseLedgers[target] = new List<DemiseShare>();

            var ledger = DemiseLedgers[target];
            var existing = ledger.FirstOrDefault(x => x.TrackingId == trackingId);
            if (existing != null) 
                existing.Shares += amount;
            else 
                ledger.Add(new DemiseShare { TrackingId = trackingId, Shares = amount });
                
            Godot.GD.Print($"[DeckTracker] Added {amount} Demise shares to {trackingId}");
        }
    }
    
    // TODO: This will literally never be called in the current game state
    public static void RemoveDemiseSharesProportionally(Creature target, decimal amountToRemove)
    {
        lock (SyncRoot)
        {
            if (!DemiseLedgers.TryGetValue(target, out var ledger)) return;

            decimal totalShares = ledger.Sum(x => x.Shares);
            if (totalShares <= 0) return;

            foreach (var share in ledger)
            {
                decimal proportion = share.Shares / totalShares;
                decimal reduction = amountToRemove * proportion;
                share.Shares = Math.Max(0, share.Shares - reduction);
            }
        }
    }
}