using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class GenericDamageTracker : PowerTrackerBase
{
    private readonly List<Contribution> _ledger = new();

    public GenericDamageTracker(string powerId) : base(powerId) { }

    public override void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            _isExecuting.Value = false;
            Log.Debug($"Reset ({PowerId}). Ledger and execution flag cleared.");
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

            _ledger.Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"LogApply ({PowerId}). Source: {trackingId}, Amount: {amount}");
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
            Log.Debug($"DistributeDamage ({PowerId}). Total Damage: {totalDamage}");

            for (var i = 0; i < _ledger.Count && remainingDamage > 0; i++)
            {
                var contribution = _ledger[i];
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
