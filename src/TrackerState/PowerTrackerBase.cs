using Godot;

namespace DeckTracker;

// Base class for trackers that are tied to a specific power and use an async execution flag.
public abstract class PowerTrackerBase : ITrackerState
{
    public string PowerId { get; }
    // Plain field rather than AsyncLocal: some powers deal damage through the game's combat
    // action/VFX queue, which runs on an execution context captured before our patch set the
    // flag, so an AsyncLocal would not flow there. Combat runs sequentially on the single main
    // thread and these windows do not overlap, so a plain field is visible for the whole window.
    protected bool _isExecuting;

    public bool IsExecuting => _isExecuting;

    protected PowerTrackerBase(string powerId)
    {
        PowerId = powerId;
    }

    public abstract void Reset();

    public void StartExecution()
    {
        _isExecuting = true;
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
            _isExecuting = false;
            Log.VeryDebug($"AwaitTaskAsync ({PowerId}) finished.");
        }
    }
}
