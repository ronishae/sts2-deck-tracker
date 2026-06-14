using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class QueueBuilderTracker : PowerTrackerBase
{
    public bool NeedsFlattening { get; }
    private readonly List<Contribution> _ledger = new();
    private readonly Queue<string> _queue = new();
    private int _spinnerIndex = 0;

    public QueueBuilderTracker(string powerId, bool needsFlattening = false) : base(powerId)
    {
        NeedsFlattening = needsFlattening;
    }

    public override void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _ledger.Clear();
            _queue.Clear();
            _isExecuting.Value = false;
            _spinnerIndex = 0;
            Log.Debug($"Reset ({PowerId}). State cleared.");
        }
    }

    public void LogApply(CardModel? cardSource, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            var id = cardSource != null ? CardRegistry.GetTrackingId(cardSource) : "External_Source";
            _ledger.Add(new Contribution { TrackingId = id, Amount = amount });
            Log.Debug($"LogApply ({PowerId}). Source: {id}, Amount: {amount}");
        }
    }

    public void AddDirectCharges(string trackingId, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            for (int i = 0; i < amount; i++)
            {
                _queue.Enqueue(trackingId);
            }
            Log.Debug($"AddDirectCharges ({PowerId}). Source: {trackingId}, Count: {amount}");
        }
    }

    public void FlattenLedgerToQueue()
    {
        lock (CardRegistry.SyncRoot)
        {
            _queue.Clear();
            foreach (var contribution in _ledger)
            {
                for (int i = 0; i < contribution.Amount; i++)
                {
                    _queue.Enqueue(contribution.TrackingId);
                }
            }
            Log.VeryDebug($"FlattenLedgerToQueue ({PowerId}). Queue size: {_queue.Count}");
        }
    }

    public string? GetNextIdForOrb()
    {
        lock (CardRegistry.SyncRoot)
        {
            // Spinner doesn't consume the queue; it iterates through it every turn using an index.
            if (PowerId == "SPINNER_POWER")
            {
                var list = _queue.ToList();
                if (_spinnerIndex < list.Count)
                {
                    var id = list[_spinnerIndex];
                    _spinnerIndex++;
                    Log.VeryDebug($"GetNextIdForOrb (Spinner). Index: {_spinnerIndex - 1}, ID: {id}");
                    return id;
                }
                return null;
            }

            if (_queue.TryDequeue(out string? dequeuedId))
            {
                Log.VeryDebug($"GetNextIdForOrb. Dequeued: {dequeuedId}");
                return dequeuedId;
            }
        }
        return null;
    }

    public int GetQueueCount()
    {
        lock (CardRegistry.SyncRoot)
        {
            return _queue.Count;
        }
    }

    // Extends base StartExecution() to include queue flattening and spinner reset before marking execution active.
    public void StartExecution(bool flatten = true)
    {
        if (flatten)
        {
            FlattenLedgerToQueue();
        }

        if (PowerId == "SPINNER_POWER")
        {
            _spinnerIndex = 0;
            Log.VeryDebug($"StartExecution ({PowerId}). Spinner index reset.");
        }

        _isExecuting.Value = true;
    }

    public async Task AwaitTaskAsync(Task originalTask, bool flatten = true)
    {
        try
        {
            StartExecution(flatten);
            await originalTask;
        }
        finally
        {
            _isExecuting.Value = false;
            lock (CardRegistry.SyncRoot)
            {
                // Storm and T2T clear the queue after every play execution
                if (flatten)
                {
                    _queue.Clear();
                    Log.Debug($"AwaitTaskAsync ({PowerId}). Queue cleared.");
                }
            }
        }
    }
}
