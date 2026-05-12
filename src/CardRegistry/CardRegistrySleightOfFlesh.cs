using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<SleightOfFleshContribution> SleightOfFleshLedger = new();
    public static readonly AsyncLocal<bool> IsSleightOfFleshExecuting = new();

    public static void ResetSleightOfFleshState()
    {
        lock (SyncRoot)
        {
            SleightOfFleshLedger.Clear();
        }
    }

    public static void LogSleightOfFleshApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            SleightOfFleshLedger.Add(new SleightOfFleshContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogSleightOfFleshApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeSleightOfFleshDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeSleightOfFleshDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < SleightOfFleshLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = SleightOfFleshLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeSleightOfFleshDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeSleightOfFleshDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitSleightOfFleshTaskAsync(Task originalTask)
    {
        try
        {
            IsSleightOfFleshExecuting.Value = true;
            await originalTask;
        }
        finally
        {
            IsSleightOfFleshExecuting.Value = false;
        }
    }
}
