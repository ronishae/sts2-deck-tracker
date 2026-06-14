using System.Runtime.CompilerServices;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class InstancedPowerTracker : ITrackerState
{
    private ConditionalWeakTable<PowerModel, string> _instanceMap = new();
    private readonly AsyncLocal<string?> _executingSourceId = new();

    public string? ExecutingSourceId => _executingSourceId.Value;

    public void Reset()
    {
        lock (CardRegistry.SyncRoot)
        {
            _instanceMap = new ConditionalWeakTable<PowerModel, string>();
            _executingSourceId.Value = null;
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
        _executingSourceId.Value = GetIdForInstance(power);
        Log.VeryDebug($"StartExecution (Instanced). Power: {power.Id.Entry}, Mapped Source: {_executingSourceId.Value}");
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
            _executingSourceId.Value = null;
            Log.VeryDebug($"AwaitTaskAsync (Instanced) finished. Power: {power.Id.Entry}");
        }
    }
}
