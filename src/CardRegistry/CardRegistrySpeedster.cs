using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<SpeedsterContribution> _speedsterLedger = new();
    private static readonly AsyncLocal<bool> _isSpeedsterExecuting = new();

    public static bool IsSpeedsterExecuting => _isSpeedsterExecuting.Value;

    public static void StartSpeedsterExecution() => _isSpeedsterExecuting.Value = true;

    public static void ResetSpeedsterState()
    {
        lock (SyncRoot)
        {
            _speedsterLedger.Clear();
        }
    }

    public static void LogSpeedsterApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _speedsterLedger.Add(new SpeedsterContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogSpeedsterApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeSpeedsterDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeSpeedsterDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _speedsterLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _speedsterLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeSpeedsterDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeSpeedsterDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitSpeedsterTaskAsync(Task originalTask)
    {
        try
        {
            StartSpeedsterExecution();
            await originalTask;
        }
        finally
        {
            _isSpeedsterExecuting.Value = false;
        }
    }
}
