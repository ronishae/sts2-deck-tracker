using Godot;

namespace DeckTracker;

// Base class for trackers that are tied to a specific power and use an async execution flag.
public abstract class PowerTrackerBase : ITrackerState
{
    public string PowerId { get; }
    protected readonly AsyncLocal<bool> _isExecuting = new();

    public bool IsExecuting => _isExecuting.Value;

    protected PowerTrackerBase(string powerId)
    {
        PowerId = powerId;
    }

    public abstract void Reset();

    public void StartExecution()
    {
        _isExecuting.Value = true;
        Log.VeryDebug($"StartExecution ({PowerId}).");
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
            Log.VeryDebug($"AwaitTaskAsync ({PowerId}) finished.");
        }
    }
}
