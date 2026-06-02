using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public enum HandoffStrategy
{
    ExactFifo,
    Proportional
}

public class BuffHandoffTracker : ITrackerState
{
    public string PowerId { get; }
    public string TargetPersistentLedgerId { get; }
    public HandoffStrategy Strategy { get; }
    
    private readonly AsyncLocal<bool> _isExecuting = new();

    public bool IsExecuting
    {
        get
        {
            return _isExecuting.Value;
        }
    }

    public BuffHandoffTracker(string powerId, string targetPersistentLedgerId, HandoffStrategy strategy = HandoffStrategy.ExactFifo)
    {
        PowerId = powerId;
        TargetPersistentLedgerId = targetPersistentLedgerId;
        Strategy = strategy;
    }

    public void Reset()
    {
        _isExecuting.Value = false;
        GD.Print($"[DeckTracker] Reset ({PowerId}). Execution flag cleared.");
    }

    public void StartExecution()
    {
        _isExecuting.Value = true;
        GD.Print($"[DeckTracker] StartExecution ({PowerId}).");
    }

    public void ProcessHandoff(string secondaryBuffId, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }

        lock (CardRegistry.SyncRoot)
        {
            GD.Print($"[DeckTracker] ProcessHandoff ({PowerId}). Target: {secondaryBuffId}, Amount: {amount}, Strategy: {Strategy}");
            if (CardRegistry.PersistentLedgers.TryGetValue(TargetPersistentLedgerId, out var ledger))
            {
                if (Strategy == HandoffStrategy.ExactFifo)
                {
                    decimal remainingToHandOff = amount;
                    foreach (var contribution in ledger)
                    {
                        if (remainingToHandOff <= 0)
                        {
                            break;
                        }
                        decimal handoffAmount = Math.Min(remainingToHandOff, contribution.Amount);
                        CardRegistry.AddPersistentBuffById(secondaryBuffId, handoffAmount, contribution.TrackingId);
                        remainingToHandOff -= handoffAmount;
                        GD.Print($"[DeckTracker]   -> Handed off {handoffAmount} to {contribution.TrackingId}");
                    }
                    if (remainingToHandOff > 0)
                    {
                        CardRegistry.AddPersistentBuffById(secondaryBuffId, remainingToHandOff, "External_Buff");
                        GD.Print($"[DeckTracker]   -> Handed off remainder {remainingToHandOff} to External_Buff");
                    }
                }
                else if (Strategy == HandoffStrategy.Proportional)
                {
                    decimal totalPool = ledger.Sum(c => c.Amount);
                    if (totalPool > 0)
                    {
                        foreach (var contribution in ledger)
                        {
                            decimal share = amount * (contribution.Amount / totalPool);
                            if (share > 0)
                            {
                                CardRegistry.AddConsumableBuffById(secondaryBuffId, share, contribution.TrackingId);
                                GD.Print($"[DeckTracker]   -> Proportional handoff {share:F2} to {contribution.TrackingId}");
                            }
                        }
                    }
                    else
                    {
                        CardRegistry.AddConsumableBuffById(secondaryBuffId, amount, "External_Buff");
                        GD.Print($"[DeckTracker]   -> Handoff fallback to External_Buff (Empty Pool)");
                    }
                }
            }
            else
            {
                // Fallback for empty ledgers
                if (Strategy == HandoffStrategy.ExactFifo)
                {
                    CardRegistry.AddPersistentBuffById(secondaryBuffId, amount, "External_Buff");
                }
                else
                {
                    CardRegistry.AddConsumableBuffById(secondaryBuffId, amount, "External_Buff");
                }
                GD.Print($"[DeckTracker]   -> Handoff fallback to External_Buff (No Ledger)");
            }
        }
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