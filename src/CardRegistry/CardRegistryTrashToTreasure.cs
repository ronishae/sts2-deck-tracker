using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- TRASH TO TREASURE TRACKING ---
    public class TrashToTreasureContribution
    {
        public string TrackingId { get; set; } = "";
        public decimal Shares { get; set; }
    }

    public static readonly List<TrashToTreasureContribution> TrashToTreasureShares = new();
    public static readonly AsyncLocal<bool> IsTrashToTreasureExecuting = new();
    public static readonly AsyncLocal<Queue<string>> TrashToTreasureAttributionQueue = new();

    public static void AddTrashToTreasureShares(decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;
        
        string trackingId = cardSource != null ? GetTrackingId(cardSource) : 
            (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value) ? "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value : "External_Source");
            
        lock (SyncRoot)
        {
            var existing = TrashToTreasureShares.FirstOrDefault(x => x.TrackingId == trackingId);
            if (existing != null) existing.Shares += amount;
            else TrashToTreasureShares.Add(new TrashToTreasureContribution { TrackingId = trackingId, Shares = amount });
            
            GD.Print($"[DeckTracker] Added {amount} TrashToTreasure shares to {trackingId}");
        }
    }
}