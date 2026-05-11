using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public class CountdownContribution
    {
        public string TrackingId { get; set; } = "";
        public decimal Amount { get; set; }
    }

    // Tracks the exact order cards added Countdown to the player
    public static readonly List<CountdownContribution> CountdownHistory = new();
    
    // The Execution Scope Trap
    public static readonly AsyncLocal<bool> IsCountdownExecuting = new();

    public static void ResetCountdownState()
    {
        lock (SyncRoot)
        {
            CountdownHistory.Clear();
        }
    }

    public static void AddCountdownHistory(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            var uniqueId = GetTrackingId(cardSource);
            CountdownHistory.Add(new CountdownContribution { TrackingId = uniqueId, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} Countdown to history for {uniqueId}.");
        }
        Publish();
    }

    // The wrapper for the Harmony Postfix
    public static async Task AwaitCountdownTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsCountdownExecuting.Value = false;
        }
    }
}