using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- ENVENOM TRACKING ---
    public static readonly AsyncLocal<bool> IsEnvenomExecuting = new();
    public static readonly List<PoisonContribution> EnvenomShares = new();

    public static void AddEnvenomShares(decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;
        
        // Let it fall back to Relics/External if cardSource is null!
        string trackingId = cardSource != null ? GetTrackingId(cardSource) : 
            (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value) ? "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value : "External_Source");
            
        lock (SyncRoot)
        {
            var existing = EnvenomShares.FirstOrDefault(x => x.TrackingId == trackingId);
            if (existing != null) existing.Shares += amount;
            else EnvenomShares.Add(new PoisonContribution { TrackingId = trackingId, Shares = amount });
        }
    }

    public static void RemoveEnvenomSharesProportionally(decimal amountToRemove)
    {
        if (amountToRemove <= 0) return;
        lock (SyncRoot)
        {
            decimal total = EnvenomShares.Sum(x => x.Shares);
            if (total <= 0) return;
            foreach (var share in EnvenomShares)
            {
                decimal proportion = share.Shares / total;
                share.Shares -= amountToRemove * proportion;
            }
            EnvenomShares.RemoveAll(x => x.Shares <= 0.01m); // Cleanup negligible decimals
        }
    }
}