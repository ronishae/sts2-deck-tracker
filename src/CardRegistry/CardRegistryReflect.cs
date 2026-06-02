using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly List<string> ReflectQueue = new();
    private static readonly AsyncLocal<bool> _isReflectExecuting = new();

    public static bool IsReflectExecuting => _isReflectExecuting.Value;

    public static void ResetReflectState()
    {
        lock (SyncRoot)
        {
            ReflectQueue.Clear();
        }
    }

    public static void AddReflect(string trackingId)
    {
        lock (SyncRoot)
        {
            ReflectQueue.Add(trackingId);
        }
    }

    public static void DecrementReflect()
    {
        lock (SyncRoot)
        {
            if (ReflectQueue.Count > 0)
            {
                ReflectQueue.RemoveAt(0);
            }
        }
    }

    public static void StartReflectExecution()
    {
        _isReflectExecuting.Value = true;
    }

    public static async Task AwaitReflectTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            _isReflectExecuting.Value = false;
        }
    }

    public static void DistributeReflectDamage(decimal amount)
    {
        lock (SyncRoot)
        {
            if (ReflectQueue.Count > 0)
            {
                GD.Print($"[DeckTracker] Distributing {amount} Reflect damage to {ReflectQueue[0]}");
                AddDamageById(ReflectQueue[0], amount);
            }
        }
    }
}
