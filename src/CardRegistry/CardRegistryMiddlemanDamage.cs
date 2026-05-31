namespace DeckTracker;

public partial class CardRegistry
{
    // --- MIDDLEMAN EXECUTION FLAGS ---
    public static readonly AsyncLocal<bool> IsInfernoExecuting = new();
    public static readonly AsyncLocal<bool> IsOutbreakExecuting = new();
    public static readonly AsyncLocal<bool> IsSmokestackExecuting = new();
    public static AsyncLocal<MegaCrit.Sts2.Core.Models.Powers.PanachePower> ExecutingPanache = new();

    // --- PROPORTIONAL LEDGERS (Player Buffs) ---
    public class PowerShare
    {
        public string TrackingId { get; set; } = "";
        public decimal Shares { get; set; }
    }

    public static List<PowerShare> InfernoLedger = new();
    public static List<PowerShare> OutbreakLedger = new();
    public static List<PowerShare> SmokestackLedger = new();
    
    // --- INSTANCED LEDGER ---
    public static Dictionary<MegaCrit.Sts2.Core.Models.Powers.PanachePower, string> PanacheLedgers = new();

    // Add this Helper Method to easily manage the proportional stacks:
    public static void AddProportionalShare(List<PowerShare> ledger, decimal amount, string trackingId)
    {
        lock (SyncRoot)
        {
            var existing = ledger.FirstOrDefault(x => x.TrackingId == trackingId);
            if (existing != null) existing.Shares += amount;
            else ledger.Add(new PowerShare { TrackingId = trackingId, Shares = amount });
        }
    }

    public static void RemoveProportionalShare(List<PowerShare> ledger, decimal amountToRemove)
    {
        lock (SyncRoot)
        {
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