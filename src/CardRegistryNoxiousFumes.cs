using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public class FumesContribution
    {
        public string TrackingId { get; set; } = "";
        public decimal Shares { get; set; }
    }

    // The bucket for Noxious Fumes applications
    public static readonly List<FumesContribution> FumesShares = new();
    
    public static readonly AsyncLocal<bool> IsNoxiousFumesExecuting = new();

    public static void ResetFumesState()
    {
        lock (SyncRoot)
        {
            FumesShares.Clear();
        }
    }

    // Called when Noxious Fumes Power goes UP
    public static void AddFumesShares(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            string uniqueId = GetTrackingId(cardSource);
            var existing = FumesShares.FirstOrDefault(c => c.TrackingId == uniqueId);
            
            if (existing != null) 
            {
                existing.Shares += amount;
            } 
            else 
            {
                FumesShares.Add(new FumesContribution { TrackingId = uniqueId, Shares = amount });
            }
            GD.Print($"[DeckTracker] Added {amount} Noxious Fumes shares to {uniqueId}.");
        }
        Publish();
    }
    
    public static async Task AwaitFumesTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsNoxiousFumesExecuting.Value = false;
        }
    }
}