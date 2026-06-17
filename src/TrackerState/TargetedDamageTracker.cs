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
            Log.Debug($"Reset (Targeted: {PowerId}). Ledgers and execution flag cleared.");
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

            var trackingId = CardRegistry.GetCurrentSourceId(cardSource);
            ledger.Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"LogApply (Targeted: {PowerId}). Target: {target.Name}, Source: {trackingId}, Amount: {amount}");
        }
    }

    public void ClearTarget(Creature target)
    {
        lock (CardRegistry.SyncRoot)
        {
            if (_ledgers.Remove(target))
            {
                Log.Debug($"ClearTarget ({PowerId}). Target: {target.Name}");
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
                Log.Warn($"DistributeDamage ({PowerId}). No ledger found for {target.Name}");
                return;
            }

            var remainingDamage = totalDamage;
            Log.Debug($"DistributeDamage ({PowerId}). Target: {target.Name}, Total Damage: {totalDamage}");

            for (var i = 0; i < ledger.Count && remainingDamage > 0; i++)
            {
                var contribution = ledger[i];
                var share = Math.Min(remainingDamage, contribution.Amount);

                if (share > 0)
                {
                    CardRegistry.AddDamageById(contribution.TrackingId, share);
                    remainingDamage -= share;
                    Log.VeryDebug($"  -> Attributed {share} to {contribution.TrackingId}. Remaining: {remainingDamage}");
                }
            }

            if (remainingDamage > 0)
            {
                Log.Warn($"  -> {remainingDamage} damage unattributed (ledger exhausted).");
            }
        }
    }
}
