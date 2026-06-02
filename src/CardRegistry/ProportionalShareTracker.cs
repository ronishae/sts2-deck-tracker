using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class ProportionalShareTracker : ITrackerState
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

    public ProportionalShareTracker(string powerId)
    {
        PowerId = powerId;
    }

    public void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            _isExecuting.Value = false;
            GD.Print($"[DeckTracker] Reset ({PowerId}). Ledger cleared.");
        }
    }

    public void AddShares(decimal amount, string trackingId)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            var existing = _ledger.FirstOrDefault(x => x.TrackingId == trackingId);
            if (existing != null)
            {
                existing.Amount += amount;
            }
            else
            {
                _ledger.Add(new PowerContribution { TrackingId = trackingId, Amount = amount });
            }
            GD.Print($"[DeckTracker] AddShares ({PowerId}). Source: {trackingId}, Amount: {amount}");
        }
    }

    public void RemoveSharesProportionally(decimal amountToRemove)
    {
        if (amountToRemove <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            decimal totalShares = _ledger.Sum(x => x.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            if (amountToRemove >= totalShares)
            {
                _ledger.Clear();
                GD.Print($"[DeckTracker] RemoveSharesProportionally ({PowerId}). Amount {amountToRemove} >= total {totalShares}. Ledger wiped.");
                return;
            }

            GD.Print($"[DeckTracker] RemoveSharesProportionally ({PowerId}). Removing {amountToRemove} from {totalShares} total shares.");
            foreach (var share in _ledger)
            {
                decimal proportion = share.Amount / totalShares;
                decimal reduction = amountToRemove * proportion;
                share.Amount = Math.Max(0, share.Amount - reduction);
                GD.Print($"[DeckTracker]   -> Reduced {share.TrackingId} by {reduction:F2}");
            }
            _ledger.RemoveAll(x => x.Amount <= 0.01m);
        }
    }

    public void Clear()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            GD.Print($"[DeckTracker] Clear ({PowerId}). Ledger wiped.");
        }
    }

    public void DistributeProportional(decimal totalAmount, Action<string, decimal> distributionAction, string contextName)
    {
        if (totalAmount <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            decimal totalShares = _ledger.Sum(x => x.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            GD.Print($"[DeckTracker] DistributeProportional ({PowerId}). Context: {contextName}, Total: {totalAmount}");
            foreach (var share in _ledger)
            {
                decimal proportion = share.Amount / totalShares;
                decimal attributed = totalAmount * proportion;
                
                if (attributed > 0)
                {
                    distributionAction(share.TrackingId, attributed);
                    GD.Print($"[DeckTracker]   -> {share.TrackingId}: {attributed:F2}");
                }
            }
        }
    }

    public void DistributeDamage(decimal totalDamage)
    {
        DistributeProportional(totalDamage, (id, amt) => CardRegistry.AddDamageById(id, amt), "Damage");
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