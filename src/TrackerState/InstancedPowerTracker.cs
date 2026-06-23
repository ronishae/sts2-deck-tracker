using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class InstancedPowerTracker : ITrackerState
{
    private ConditionalWeakTable<PowerModel, string> _instanceMap = new();

    // Plain field rather than AsyncLocal: instanced powers (e.g. Rolling Boulder) deal their
    // damage through the game's combat action/VFX queue, which runs on an execution context
    // captured before our patch set the value, so an AsyncLocal would not flow there. Combat
    // runs sequentially on the single main thread and execution windows do not overlap, so a
    // plain field is visible to the queue-dispatched damage for the whole window.
    private string? _executingSourceId;

    public string? ExecutingSourceId => _executingSourceId;

    public void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _instanceMap = new ConditionalWeakTable<PowerModel, string>();
            _executingSourceId = null;
            Log.Debug("Reset (InstancedTracker). Memory map cleared.");
        }
    }

    public void LogInstance(PowerModel power, CardModel? cardSource, string fallback = "External_Source")
    {
        if (power == null)
        {
            return;
        }
        lock (CardRegistry.SyncRoot)
        {
            if (!_instanceMap.TryGetValue(power, out _))
            {
                string id = cardSource != null ? CardRegistry.GetTrackingId(cardSource) : fallback;
                _instanceMap.Add(power, id);
                Log.Debug($"LogInstance. Power: {power.Id.Entry} ({power.GetHashCode()}) mapped to {id}");
            }
        }
    }

    public string? GetIdForInstance(PowerModel power)
    {
        if (power == null)
        {
            return null;
        }
        _instanceMap.TryGetValue(power, out string? id);
        return id;
    }

    public void StartExecution(PowerModel power)
    {
        _executingSourceId = GetIdForInstance(power);
        Log.VeryDebug($"StartExecution (Instanced). Power: {power.Id.Entry}, Mapped Source: {_executingSourceId}");
    }

    public async Task AwaitTaskAsync(Task originalTask, PowerModel power)
    {
        try
        {
            StartExecution(power);
            await originalTask;
        }
        finally
        {
            _executingSourceId = null;
            Log.VeryDebug($"AwaitTaskAsync (Instanced) finished. Power: {power.Id.Entry}");
        }
    }
}
