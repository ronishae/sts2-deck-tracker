using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Tracks the exact order cards added Countdown to the player
    public static readonly List<Contribution> CountdownHistory = new();
    
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
            CountdownHistory.Add(new Contribution { TrackingId = uniqueId, Amount = amount });
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