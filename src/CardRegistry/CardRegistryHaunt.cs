using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<HauntContribution> _hauntLedger = new();
    private static readonly AsyncLocal<bool> _isHauntExecuting = new();

    public static bool IsHauntExecuting => _isHauntExecuting.Value;

    public static void StartHauntExecution() => _isHauntExecuting.Value = true;

    public static void ResetHauntState()
    {
        lock (SyncRoot)
        {
            _hauntLedger.Clear();
        }
    }

    public static void LogHauntApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _hauntLedger.Add(new HauntContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogHauntApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeHauntDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeHauntDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _hauntLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _hauntLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeHauntDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeHauntDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitHauntTaskAsync(Task originalTask)
    {
        try
        {
            StartHauntExecution();
            await originalTask;
        }
        finally
        {
            _isHauntExecuting.Value = false;
        }
    }
}
