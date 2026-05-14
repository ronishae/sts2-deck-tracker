using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<HailstormContribution> _hailstormLedger = new();
    private static readonly AsyncLocal<bool> _isHailstormExecuting = new();

    public static bool IsHailstormExecuting => _isHailstormExecuting.Value;

    public static void StartHailstormExecution() => _isHailstormExecuting.Value = true;

    public static void ResetHailstormState()
    {
        lock (SyncRoot)
        {
            _hailstormLedger.Clear();
        }
    }

    public static void LogHailstormApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _hailstormLedger.Add(new HailstormContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogHailstormApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeHailstormDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeHailstormDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _hailstormLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _hailstormLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeHailstormDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeHailstormDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitHailstormTaskAsync(Task originalTask)
    {
        try
        {
            StartHailstormExecution();
            await originalTask;
        }
        finally
        {
            _isHailstormExecuting.Value = false;
        }
    }
}
