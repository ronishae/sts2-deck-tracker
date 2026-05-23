namespace DeckTracker;

public partial class CardRegistry
{
    public static readonly AsyncLocal<bool> IsDemonFormExecuting = new();
    
    public static async Task AwaitDemonFormTaskAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsDemonFormExecuting.Value = false; }
    }

    public static void ProcessDemonFormStrength(decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (PersistentLedgers.TryGetValue("DemonFormPower", out var ledger))
            {
                // EXACT FIFO HANDOFF
                // Maps the generated Strength perfectly 1-to-1 with who owns the Demon Form stacks!
                decimal remainingToHandOff = amount;
                foreach (var contribution in ledger)
                {
                    if (remainingToHandOff <= 0) break;
                    decimal handoffAmount = Math.Min(remainingToHandOff, contribution.Amount);
                    
                    AddPersistentBuffById("StrengthPower", handoffAmount, contribution.TrackingId);
                    remainingToHandOff -= handoffAmount;
                }
                return;
            }
            // Fallback just in case
            AddPersistentBuffById("StrengthPower", amount, "External_Buff");
        }
    }
}