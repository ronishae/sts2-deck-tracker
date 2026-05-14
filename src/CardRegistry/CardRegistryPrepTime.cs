using Godot;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- STATE VARIABLES (Add this near your other AsyncLocals) ---
    public static readonly AsyncLocal<bool> IsPrepTimeExecuting = new();

    // --- PREP TIME HANDOFF LOGIC ---

    public static async Task AwaitPrepTimeTaskAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsPrepTimeExecuting.Value = false; }
    }

    // A helper to add consumable buffs when we already know the exact ID
    public static void AddConsumableBuffById(string buffType, decimal amount, string trackingId)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.ContainsKey(buffType)) ConsumableLedgers[buffType] = new List<BuffContribution>();
            ConsumableLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] Handoff: Added {amount} {buffType} to FIFO ledger for {trackingId}");
        }
    }

    public static void ProcessPrepTimeVigor(decimal amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            if (PersistentLedgers.TryGetValue("PrepTimePower", out var ledger))
            {
                // Calculate total PrepTime pool
                decimal totalPool = 0;
                foreach (var c in ledger) totalPool += c.Amount;

                if (totalPool > 0)
                {
                    // Distribute the newly generated Vigor based on who owns the PrepTime!
                    foreach (var contribution in ledger)
                    {
                        decimal share = amount * (contribution.Amount / totalPool);
                        if (share > 0) AddConsumableBuffById("VigorPower", share, contribution.TrackingId);
                    }
                    return;
                }
            }
            
            // Fallback just in case the ledger is empty for some reason
            AddConsumableBuffById("VigorPower", amount, "External_Buff");
        }
    }
}