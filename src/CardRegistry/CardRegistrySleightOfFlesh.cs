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
    private static readonly List<SleightOfFleshContribution> _sleightOfFleshLedger = new();
    private static readonly AsyncLocal<bool> _isSleightOfFleshExecuting = new();

    public static bool IsSleightOfFleshExecuting => _isSleightOfFleshExecuting.Value;

    public static void StartSleightOfFleshExecution() => _isSleightOfFleshExecuting.Value = true;

    public static void ResetSleightOfFleshState()
    {
        lock (SyncRoot)
        {
            _sleightOfFleshLedger.Clear();
        }
    }

    public static void LogSleightOfFleshApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _sleightOfFleshLedger.Add(new SleightOfFleshContribution { TrackingId = trackingId, Amount = amount });
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
            for (var i = 0; i < _sleightOfFleshLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _sleightOfFleshLedger[i];
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
            StartSleightOfFleshExecution();
            await originalTask;
        }
        finally
        {
            _isSleightOfFleshExecuting.Value = false;
        }
    }
}
