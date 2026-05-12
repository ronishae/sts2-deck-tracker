using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<BlackHoleContribution> _blackHoleLedger = new();
    private static readonly AsyncLocal<bool> _isBlackHoleExecuting = new();

    public static bool IsBlackHoleExecuting => _isBlackHoleExecuting.Value;

    public static void StartBlackHoleExecution() => _isBlackHoleExecuting.Value = true;

    public static void ResetBlackHoleState()
    {
        lock (SyncRoot)
        {
            _blackHoleLedger.Clear();
        }
    }

    public static void LogBlackHoleApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _blackHoleLedger.Add(new BlackHoleContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogBlackHoleApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeBlackHoleDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeBlackHoleDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _blackHoleLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _blackHoleLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
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
            StartBlackHoleExecution();
            await originalTask;
        }
        finally
        {
            _isBlackHoleExecuting.Value = false;
        }
    }
}
