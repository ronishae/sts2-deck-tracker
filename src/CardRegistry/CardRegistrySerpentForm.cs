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
    private static readonly List<SerpentFormContribution> _serpentFormLedger = new();
    private static readonly AsyncLocal<bool> _isSerpentFormExecuting = new();

    public static bool IsSerpentFormExecuting => _isSerpentFormExecuting.Value;

    public static void StartSerpentFormExecution() => _isSerpentFormExecuting.Value = true;

    public static void ResetSerpentFormState()
    {
        lock (SyncRoot)
        {
            _serpentFormLedger.Clear();
        }
    }

    public static void LogSerpentFormApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _serpentFormLedger.Add(new SerpentFormContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogSerpentFormApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeSerpentFormDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeSerpentFormDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _serpentFormLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _serpentFormLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeSerpentFormDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeSerpentFormDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitSerpentFormTaskAsync(Task originalTask)
    {
        try
        {
            StartSerpentFormExecution();
            await originalTask;
        }
        finally
        {
            _isSerpentFormExecuting.Value = false;
        }
    }
}
