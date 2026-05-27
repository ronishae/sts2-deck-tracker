namespace DeckTracker;

public partial class CardRegistry
{
    // --- STATE VARIABLES ---
    public static readonly AsyncLocal<bool> IsMonologueExecuting = new();

    // --- MONOLOGUE HANDOFF LOGIC ---

    public static async Task AwaitMonologueTaskAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsMonologueExecuting.Value = false; }
    }

    public static void ProcessMonologueStrength(decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (PersistentLedgers.TryGetValue("MONOLOGUE_POWER", out var ledger))
            {
                // EXACT FIFO HANDOFF
                // Because Monologue instances all do the same thing, the FIFO queue will perfectly 
                // distribute the 1 Strength to the oldest active Monologue card!
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
            AddPersistentBuffById("STRENGTH_POWER", amount, "External_Buff");
        }
    }
}