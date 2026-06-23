using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // The bucket for Reaper Form applications
    public static readonly List<Contribution> ReaperFormShares = new();
    
    public static bool IsReaperFormExecuting;
    private static decimal _reaperDamage;

    public static void StartReaperFormExecution(decimal damage)
    {
        IsReaperFormExecuting = true;
        _reaperDamage = damage;
    }

    public static decimal GetReaperDamage() => _reaperDamage;

    public static void ResetReaperFormState()
    {
        lock (SyncRoot)
        {
            ReaperFormShares.Clear();
            _reaperDamage = 0m;
        }
    }

    // Called when Reaper Form Power goes UP
    public static void AddReaperFormShares(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            var uniqueId = GetTrackingId(cardSource);
            var existing = ReaperFormShares.FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Amount += amount;
            } 
            else 
            {
                ReaperFormShares.Add(new Contribution { TrackingId = uniqueId, Amount = amount });
            }
            Log.Debug($"Added {amount} Reaper Form shares to {uniqueId}.");
        }
        Publish();
    }
    
    public static async Task AwaitReaperFormTaskAsync(Task originalTask, decimal damage)
    {
        try
        {
            StartReaperFormExecution(damage);
            await originalTask;
        }
        finally
        {
            IsReaperFormExecuting = false;
        }
    }
}
