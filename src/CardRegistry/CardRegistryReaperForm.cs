using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // The bucket for Reaper Form applications
    public static readonly List<ReaperFormContribution> ReaperFormShares = new();
    
    public static readonly AsyncLocal<bool> IsReaperFormExecuting = new();
    private static AsyncLocal<decimal> _reaperDamage = new();

    public static void StartReaperFormExecution(decimal damage) 
    {
        IsReaperFormExecuting.Value = true;
        _reaperDamage.Value = damage;
    }

    public static decimal GetReaperDamage() => _reaperDamage.Value;

    public static void ResetReaperFormState()
    {
        lock (SyncRoot)
        {
            ReaperFormShares.Clear();
            _reaperDamage = new AsyncLocal<decimal>();
        }
    }

    // Called when Reaper Form Power goes UP
    public static void AddReaperFormShares(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            string uniqueId = GetTrackingId(cardSource);
            var existing = ReaperFormShares.FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Shares += amount;
            } 
            else 
            {
                ReaperFormShares.Add(new ReaperFormContribution { TrackingId = uniqueId, Shares = amount });
            }
            GD.Print($"[DeckTracker] Added {amount} Reaper Form shares to {uniqueId}.");
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
            IsReaperFormExecuting.Value = false;
        }
    }
}
