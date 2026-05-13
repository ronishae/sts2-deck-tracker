using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<ThunderContribution> _thunderLedger = new();
    private static readonly AsyncLocal<bool> _isThunderExecuting = new();

    public static bool IsThunderExecuting => _isThunderExecuting.Value;

    public static void StartThunderExecution() => _isThunderExecuting.Value = true;

    public static void ResetThunderState()
    {
        lock (SyncRoot)
        {
            _thunderLedger.Clear();
        }
    }

    public static void LogThunderApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _thunderLedger.Add(new ThunderContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogThunderApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeThunderDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeThunderDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _thunderLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _thunderLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeThunderDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeThunderDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitThunderTaskAsync(Task originalTask)
    {
        try
        {
            StartThunderExecution();
            await originalTask;
        }
        finally
        {
            _isThunderExecuting.Value = false;
        }
    }
}
