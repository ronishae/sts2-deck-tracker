using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<BlackHoleContribution> BlackHoleLedger = new();
    public static readonly AsyncLocal<bool> IsBlackHoleExecuting = new();

    public static void ResetBlackHoleState()
    {
        lock (SyncRoot)
        {
            BlackHoleLedger.Clear();
        }
    }

    public static void LogBlackHoleApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            BlackHoleLedger.Add(new BlackHoleContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogBlackHoleApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeBlackHoleDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            decimal remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeBlackHoleDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (int i = 0; i < BlackHoleLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = BlackHoleLedger[i];
                decimal share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeBlackHoleDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeBlackHoleDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitBlackHoleTaskAsync(Task originalTask)
    {
        try
        {
            IsBlackHoleExecuting.Value = true;
            await originalTask;
        }
        finally
        {
            IsBlackHoleExecuting.Value = false;
        }
    }
}
