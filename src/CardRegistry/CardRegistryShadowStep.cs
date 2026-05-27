using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- SHADOW STEP HANDOFF LOGIC ---
    public static readonly AsyncLocal<bool> IsShadowStepExecuting = new();

    public static async Task AwaitShadowStepTaskAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsShadowStepExecuting.Value = false; }
    }

    public static void ProcessShadowStepDoubleDamage(decimal amount, Creature player)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (PersistentLedgers.TryGetValue("SHADOW_STEP_POWER", out var ledger))
            {
                // EXACT FIFO HANDOFF (No Proportional Math!)
                // If ShadowStep A gave 1, and B gave 2, we just clone that exact integer order into DoubleDamage!
                decimal remainingToHandOff = amount;
                foreach (var contribution in ledger)
                {
                    if (remainingToHandOff <= 0) break;
                    decimal handoffAmount = Math.Min(remainingToHandOff, contribution.Amount);
                    
                    AddDurationBuff(player, "DOUBLE_DAMAGE_POWER", handoffAmount, contribution.TrackingId);
                    remainingToHandOff -= handoffAmount;
                }
                return;
            }
            AddDurationBuff(player, "DOUBLE_DAMAGE_POWER", amount, "External_Buff");
        }
    }
}