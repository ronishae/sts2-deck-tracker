using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<string> _necroMasteryLedger = new();
    private static bool _isNecroMasteryExecuting;
    private static decimal _delta;

    public static bool IsNecroMasteryExecuting => _isNecroMasteryExecuting;

    public static void StartNecroMasteryExecution(decimal delta)
    {
        _isNecroMasteryExecuting = true;
        _delta = delta;
    }

    public static void ResetNecroMasteryState()
    {
        lock (SyncRoot)
        {
            _necroMasteryLedger.Clear();
            _delta = 0m;
        }
    }

    public static void LogNecroMasteryApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _necroMasteryLedger.Add(trackingId);
            Log.Debug($"LogNecroMasteryApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeNecroMasteryDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            Log.Debug($"DistributeNecroMasteryDamage. Total Damage: {totalDamage} with delta: {_delta}");

            foreach (var trackingId in _necroMasteryLedger)
            {
                if (remainingDamage <= 0) break;
                var share = Math.Min(remainingDamage, -_delta);
                AddDamageById(trackingId, share);
                remainingDamage -= share;
                Log.Debug($"DistributeNecroMasteryDamage. Attributed {share} to {trackingId}. Remaining: {remainingDamage}");
            }

            if (remainingDamage > 0)
            {
                Log.Warn($"DistributeNecroMasteryDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitNecroMasteryTaskAsync(Task originalTask, decimal delta)
    {
        try
        {
            StartNecroMasteryExecution(delta);
            await originalTask;
        }
        finally
        {
            _isNecroMasteryExecuting = false;
            Log.VeryDebug("AwaitNecroMasteryTaskAsync finished.");
        }
    }
}
