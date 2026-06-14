using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<string> _necroMasteryLedger = new();
    private static readonly AsyncLocal<bool> _isNecroMasteryExecuting = new();
    private static AsyncLocal<decimal> _delta = new();

    public static bool IsNecroMasteryExecuting => _isNecroMasteryExecuting.Value;

    public static void StartNecroMasteryExecution(decimal delta)
    {
        _isNecroMasteryExecuting.Value = true;
        _delta.Value = delta;
    }

    public static void ResetNecroMasteryState()
    {
        lock (SyncRoot)
        {
            _necroMasteryLedger.Clear();
            _delta = new AsyncLocal<decimal>();
        }
    }

    public static void LogNecroMasteryApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            _necroMasteryLedger.Add(trackingId);
            GD.Print($"[DeckTracker] LogNecroMasteryApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeNecroMasteryDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            GD.Print($"[DeckTracker] DistributeNecroMasteryDamage. Total Damage: {totalDamage} with delta: {_delta.Value}");

            foreach (var trackingId in _necroMasteryLedger)
            {
                if (remainingDamage <= 0) break;
                var share = Math.Min(remainingDamage, -_delta.Value);
                AddDamageById(trackingId, share);
                remainingDamage -= share;
                GD.Print($"[DeckTracker] DistributeNecroMasteryDamage. Attributed {share} to {trackingId}. Remaining: {remainingDamage}");
            }

            if (remainingDamage > 0)
            {
                GD.Print($"[DeckTracker] DistributeNecroMasteryDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
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
            _isNecroMasteryExecuting.Value = false;
            GD.Print("[DeckTracker] AwaitNecroMasteryTaskAsync finished.");
        }
    }
}
