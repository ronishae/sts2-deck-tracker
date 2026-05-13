using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<ThornsContribution> _thornsLedger = new();
    private static readonly AsyncLocal<bool> _isThornsExecuting = new();

    public static bool IsThornsExecuting => _isThornsExecuting.Value;

    public static void StartThornsExecution() => _isThornsExecuting.Value = true;

    public static void ResetThornsState()
    {
        lock (SyncRoot)
        {
            _thornsLedger.Clear();
        }
    }

    public static void LogThornsApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _thornsLedger.Add(new ThornsContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogThornsApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeThornsDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeThornsDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _thornsLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _thornsLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeThornsDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeThornsDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitThornsTaskAsync(Task originalTask)
    {
        try
        {
            StartThornsExecution();
            await originalTask;
        }
        finally
        {
            _isThornsExecuting.Value = false;
        }
    }
}
