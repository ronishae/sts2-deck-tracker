using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<FlameBarrierContribution> _flameBarrierLedger = new();
    private static readonly AsyncLocal<bool> _isFlameBarrierExecuting = new();

    public static bool IsFlameBarrierExecuting => _isFlameBarrierExecuting.Value;

    public static void StartFlameBarrierExecution() => _isFlameBarrierExecuting.Value = true;

    public static void ResetFlameBarrierState()
    {
        lock (SyncRoot)
        {
            _flameBarrierLedger.Clear();
        }
    }

    public static void LogFlameBarrierApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _flameBarrierLedger.Add(new FlameBarrierContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogFlameBarrierApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void ClearFlameBarrier()
    {
        lock (SyncRoot)
        {
            _flameBarrierLedger.Clear();
            GD.Print("[DeckTracker] ClearFlameBarrier. Ledger cleared.");
        }
    }

    public static void DistributeFlameBarrierDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeFlameBarrierDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _flameBarrierLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _flameBarrierLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeFlameBarrierDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeFlameBarrierDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitFlameBarrierTaskAsync(Task originalTask)
    {
        try
        {
            StartFlameBarrierExecution();
            await originalTask;
        }
        finally
        {
            _isFlameBarrierExecuting.Value = false;
        }
    }
}
