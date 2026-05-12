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
    private static readonly List<SerpentFormContribution> SerpentFormLedger = new();
    public static readonly AsyncLocal<bool> IsSerpentFormExecuting = new();

    public static void ResetSerpentFormState()
    {
        lock (SyncRoot)
        {
            SerpentFormLedger.Clear();
        }
    }

    public static void LogSerpentFormApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            SerpentFormLedger.Add(new SerpentFormContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogSerpentFormApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeSerpentFormDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            decimal remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeSerpentFormDamage. Total Damage: {totalDamage}");

            // Distribute damage in FIFO order based on contributions
            for (int i = 0; i < SerpentFormLedger.Count && remainingDamage > 0; i++)
            {
                var contribution = SerpentFormLedger[i];
                decimal share = Math.Min(remainingDamage, (decimal)contribution.Amount);
                
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
            IsSerpentFormExecuting.Value = true;
            await originalTask;
        }
        finally
        {
            IsSerpentFormExecuting.Value = false;
        }
    }
}
