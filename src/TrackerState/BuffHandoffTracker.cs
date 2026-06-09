using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;

namespace DeckTracker;

public enum HandoffStrategy
{
    ExactFifo,
    Proportional
}

public class BuffHandoffTracker : PowerTrackerBase
{
    public string TargetPersistentLedgerId { get; }
    public HandoffStrategy Strategy { get; }

    public BuffHandoffTracker(string powerId, string targetPersistentLedgerId, HandoffStrategy strategy = HandoffStrategy.ExactFifo)
        : base(powerId)
    {
        TargetPersistentLedgerId = targetPersistentLedgerId;
        Strategy = strategy;
    }

    public override void Reset()
    {
        _isExecuting.Value = false;
        GD.Print($"[DeckTracker] Reset ({PowerId}). Execution flag cleared.");
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
                switch (Strategy)
                {
                    case HandoffStrategy.ExactFifo:
                        ProcessExactFifoHandoff(secondaryBuffId, amount, ledger);
                        break;
                    case HandoffStrategy.Proportional:
                        ProcessProportionalHandoff(secondaryBuffId, amount, ledger);
                        break;
                }
            }
            else
            {
                // Fallback for empty ledgers
                ProcessFallbackHandoff(secondaryBuffId, amount);
            }
        }
    }

    private void ProcessExactFifoHandoff(string secondaryBuffId, decimal amount, List<Contribution> ledger)
    {
        var remainingToHandOff = amount;
        foreach (var contribution in ledger)
        {
            if (remainingToHandOff <= 0)
            {
                break;
            }
            var handoffAmount = Math.Min(remainingToHandOff, contribution.Amount);
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

    private void ProcessProportionalHandoff(string secondaryBuffId, decimal amount, List<Contribution> ledger)
    {
        var totalPool = ledger.Sum(c => c.Amount);
        if (totalPool > 0)
        {
            foreach (var contribution in ledger)
            {
                var share = amount * (contribution.Amount / totalPool);
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

    private void ProcessFallbackHandoff(string secondaryBuffId, decimal amount)
    {
        switch (Strategy)
        {
            case HandoffStrategy.ExactFifo:
                CardRegistry.AddPersistentBuffById(secondaryBuffId, amount, "External_Buff");
                break;
            case HandoffStrategy.Proportional:
                CardRegistry.AddConsumableBuffById(secondaryBuffId, amount, "External_Buff");
                break;
        }
        GD.Print($"[DeckTracker]   -> Handoff fallback to External_Buff (No Ledger)");
    }
}
