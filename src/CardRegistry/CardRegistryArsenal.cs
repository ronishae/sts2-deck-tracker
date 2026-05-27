namespace DeckTracker;

public partial class CardRegistry
{
    // --- STATE VARIABLES ---
    public static readonly AsyncLocal<bool> IsArsenalExecuting = new();

    // --- ARSENAL HANDOFF LOGIC ---

    public static async Task AwaitArsenalTaskAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsArsenalExecuting.Value = false; }
    }

    public static void ProcessArsenalStrength(decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (PersistentLedgers.TryGetValue("ARSENAL_POWER", out var ledger))
            {
                // EXACT FIFO HANDOFF
                // Maps the generated Strength perfectly 1-to-1 with who owns the Arsenal stacks!
                decimal remainingToHandOff = amount;
                foreach (var contribution in ledger)
                {
                    if (remainingToHandOff <= 0) break;
                    decimal handoffAmount = Math.Min(remainingToHandOff, contribution.Amount);
                    
                    AddPersistentBuffById("STRENGTH_POWER", handoffAmount, contribution.TrackingId);
                    remainingToHandOff -= handoffAmount;
                }
                return;
            }
            // Fallback just in case
            AddPersistentBuffById("STRENGTH_POWER", amount, "External_Buff");
        }
    }
}