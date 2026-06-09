using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class TargetedDamageTracker : PowerTrackerBase
{
    private readonly Dictionary<Creature, List<Contribution>> _ledgers = new();

    public TargetedDamageTracker(string powerId) : base(powerId) { }

    public override void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledgers.Clear();
            _isExecuting.Value = false;
            GD.Print($"[DeckTracker] Reset (Targeted: {PowerId}). Ledgers and execution flag cleared.");
        }
    }

    public void LogApply(Creature target, CardModel? cardSource, decimal amount)
    {
        if (amount <= 0 || target == null)
        {
            return;
        }

        lock (CardRegistry.SyncRoot)
        {
            if (!_ledgers.TryGetValue(target, out var ledger))
            {
                ledger = new List<Contribution>();
                _ledgers[target] = ledger;
            }

            var trackingId = cardSource != null ? CardRegistry.GetTrackingId(cardSource) : "External_Source";
            ledger.Add(new Contribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogApply (Targeted: {PowerId}). Target: {target.Name}, Source: {trackingId}, Amount: {amount}");
        }
    }

    public void ClearTarget(Creature target)
    {
        lock (CardRegistry.SyncRoot)
        {
            if (_ledgers.Remove(target))
            {
                GD.Print($"[DeckTracker] ClearTarget ({PowerId}). Target: {target.Name}");
            }
        }
    }

    public void DistributeDamage(Creature target, decimal totalDamage)
    {
        if (totalDamage <= 0 || target == null)
        {
            return;
        }

        lock (CardRegistry.SyncRoot)
        {
            if (!_ledgers.TryGetValue(target, out var ledger))
            {
                GD.Print($"[DeckTracker] DistributeDamage ({PowerId}). No ledger found for {target.Name}");
                return;
            }

            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeDamage ({PowerId}). Target: {target.Name}, Total Damage: {totalDamage}");

            for (var i = 0; i < ledger.Count && remainingDamage > 0; i++)
            {
                var contribution = ledger[i];
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
}
