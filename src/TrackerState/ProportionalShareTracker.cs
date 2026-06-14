using Godot;

namespace DeckTracker;

public class ProportionalShareTracker : PowerTrackerBase
{
    private readonly List<Contribution> _ledger = new();

    public ProportionalShareTracker(string powerId) : base(powerId) { }

    public override void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            _isExecuting.Value = false;
            Log.Debug($"Reset ({PowerId}). Ledger cleared.");
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
                _ledger.Add(new Contribution { TrackingId = trackingId, Amount = amount });
            }
            Log.Debug($"AddShares ({PowerId}). Source: {trackingId}, Amount: {amount}");
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
            var totalShares = _ledger.Sum(x => x.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            if (amountToRemove >= totalShares)
            {
                _ledger.Clear();
                Log.Debug($"RemoveSharesProportionally ({PowerId}). Amount {amountToRemove} >= total {totalShares}. Ledger wiped.");
                return;
            }

            Log.Debug($"RemoveSharesProportionally ({PowerId}). Removing {amountToRemove} from {totalShares} total shares.");
            foreach (var share in _ledger)
            {
                var proportion = share.Amount / totalShares;
                var reduction = amountToRemove * proportion;
                share.Amount = Math.Max(0, share.Amount - reduction);
                Log.VeryDebug($"  -> Reduced {share.TrackingId} by {reduction:F2}");
            }
            _ledger.RemoveAll(x => x.Amount <= 0.01m);
        }
    }

    public void Clear()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            Log.Debug($"Clear ({PowerId}). Ledger wiped.");
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
            var totalShares = _ledger.Sum(x => x.Amount);
            if (totalShares <= 0)
            {
                return;
            }

            Log.Debug($"DistributeProportional ({PowerId}). Context: {contextName}, Total: {totalAmount}");
            foreach (var share in _ledger)
            {
                var proportion = share.Amount / totalShares;
                var attributed = totalAmount * proportion;

                if (attributed > 0)
                {
                    distributionAction(share.TrackingId, attributed);
                    Log.VeryDebug($"  -> {share.TrackingId}: {attributed:F2}");
                }
            }
        }
    }

    public void DistributeDamage(decimal totalDamage)
    {
        DistributeProportional(totalDamage, (id, amt) => CardRegistry.AddDamageById(id, amt), "Damage");
    }
}
