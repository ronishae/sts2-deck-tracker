using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class GenericDamageTracker : ITrackerState
{
    public string PowerId { get; }
    private readonly List<PowerContribution> _ledger = new();
    private readonly AsyncLocal<bool> _isExecuting = new();

    public bool IsExecuting
    {
        get
        {
            return _isExecuting.Value;
        }
    }

    public GenericDamageTracker(string powerId)
    {
        PowerId = powerId;
    }

    public void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            _isExecuting.Value = false;
            GD.Print($"[DeckTracker] Reset ({PowerId}). Ledger and execution flag cleared.");
        }
    }

    public void LogApply(CardModel? cardSource, decimal amount, string fallbackTrackingId = "External_Source")
    {
        if (amount <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            string trackingId;
            if (cardSource != null)
            {
                trackingId = CardRegistry.GetTrackingId(cardSource);
            }
            else
            {
                trackingId = fallbackTrackingId;
            }

            _ledger.Add(new PowerContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogApply ({PowerId}). Source: {trackingId}, Amount: {amount}");
        }
    }

    public void DistributeDamage(decimal totalDamage)
    {
        if (totalDamage <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeDamage ({PowerId}). Total Damage: {totalDamage}");

            for (var i = 0; i < _ledger.Count && remainingDamage > 0; i++)
            {
                var contribution = _ledger[i];
                var share = Math.Min(remainingDamage, contribution.Amount);
                
                if (share > 0)
                {
                    CardRegistry.AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    GD.Print($"[DeckTracker]   -> Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }
            
            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker]   -> {remainingDamage} damage unattributed (ledger exhausted).");
            }
        }
    }

    public void StartExecution()
    {
        _isExecuting.Value = true;
        GD.Print($"[DeckTracker] StartExecution ({PowerId}).");
    }

    public async Task AwaitTaskAsync(Task originalTask)
    {
        try
        {
            StartExecution();
            await originalTask;
        }
        finally
        {
            _isExecuting.Value = false;
            GD.Print($"[DeckTracker] AwaitTaskAsync ({PowerId}) finished.");
        }
    }
}