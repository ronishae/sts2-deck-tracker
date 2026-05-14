using Godot;
using MegaCrit.Sts2.Core.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly List<StormContribution> StormHistory = new();
    public static readonly List<string> CurrentTurnStormQueue = new();
    public static readonly AsyncLocal<bool> IsStormExecuting = new();

    public static void ResetStormState()
    {
        lock (SyncRoot)
        {
            StormHistory.Clear();
            CurrentTurnStormQueue.Clear();
        }
    }

    public static void LogStormApply(CardModel? cardSource, int amount)
    {
        if (cardSource == null || amount <= 0) return;
        lock (SyncRoot)
        {
            string id = GetTrackingId(cardSource);
            StormHistory.Add(new StormContribution { TrackingId = id, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} Storm to ledger for {id}");
        }
    }

    public static string? GetNextStormTrackingId()
    {
        lock (SyncRoot)
        {
            if (CurrentTurnStormQueue.Count > 0)
            {
                string id = CurrentTurnStormQueue[0];
                CurrentTurnStormQueue.RemoveAt(0);
                return id;
            }
        }
        return null;
    }

    public static async Task AwaitStormTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsStormExecuting.Value = false;
            CurrentTurnStormQueue.Clear();
        }
    }
}
