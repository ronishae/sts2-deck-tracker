using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<JuggernautContribution> _juggernautLedger = new();
    private static readonly AsyncLocal<bool> _isJuggernautExecuting = new();

    public static bool IsJuggernautExecuting => _isJuggernautExecuting.Value;

    public static void StartJuggernautExecution() => _isJuggernautExecuting.Value = true;

    public static void ResetJuggernautState()
    {
        lock (SyncRoot)
        {
            _juggernautLedger.Clear();
        }
    }

    public static void LogJuggernautApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _juggernautLedger.Add(new JuggernautContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogJuggernautApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeJuggernautDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeJuggernautDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (var i = 0; i < _juggernautLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = _juggernautLedger[i];
                var share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
                if (share > 0)
                {
                    AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker] DistributeJuggernautDamage. Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeJuggernautDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitJuggernautTaskAsync(Task originalTask)
    {
        try
        {
            StartJuggernautExecution();
            await originalTask;
        }
        finally
        {
            _isJuggernautExecuting.Value = false;
        }
    }
}
