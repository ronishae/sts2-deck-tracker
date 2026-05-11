using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    

    public static readonly List<CorrosiveWaveContribution> CorrosiveWaveShares = new();
    public static readonly AsyncLocal<bool> IsCorrosiveWaveExecuting = new();

    public static void ResetCorrosiveWaveState()
    {
        lock (SyncRoot)
        {
            CorrosiveWaveShares.Clear();
        }
    }

    public static void AddCorrosiveWaveShares(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            string uniqueId = GetTrackingId(cardSource);
            var existing = CorrosiveWaveShares.FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) existing.Shares += amount;
            else CorrosiveWaveShares.Add(new CorrosiveWaveContribution { TrackingId = uniqueId, Shares = amount });

            GD.Print($"[DeckTracker] Added {amount} Corrosive Wave shares to {uniqueId}.");
        }
        Publish();
    }
    
    public static void ClearCorrosiveWaveShares()
    {
        lock (SyncRoot)
        {
            CorrosiveWaveShares.Clear();
            GD.Print("[DeckTracker] Corrosive Wave Power removed. Cleared shares.");
        }
    }

    public static async Task AwaitCorrosiveWaveTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsCorrosiveWaveExecuting.Value = false;
        }
    }
}