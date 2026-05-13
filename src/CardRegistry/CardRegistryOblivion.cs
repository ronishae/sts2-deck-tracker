using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly Dictionary<Creature, List<OblivionContribution>> _oblivionLedgers = new();
    private static readonly AsyncLocal<bool> _isOblivionExecuting = new();

    public static bool IsOblivionExecuting => _isOblivionExecuting.Value;

    public static void StartOblivionExecution() => _isOblivionExecuting.Value = true;

    public static void ResetOblivionState()
    {
        lock (SyncRoot)
        {
            _oblivionLedgers.Clear();
        }
    }

    public static void LogOblivionApply(Creature target, CardModel? cardSource, int amount)
    {
        if (amount <= 0 || target == null) return;

        lock (SyncRoot)
        {
            if (!_oblivionLedgers.TryGetValue(target, out var ledger))
            {
                ledger = new List<OblivionContribution>();
                _oblivionLedgers[target] = ledger;
            }

            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            ledger.Add(new OblivionContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] LogOblivionApply. Target: {target.Name}, Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void ClearOblivion(Creature target)
    {
        lock (SyncRoot)
        {
            if (_oblivionLedgers.Remove(target))
            {
                GD.Print($"[DeckTracker] ClearOblivion. Target: {target.Name}");
            }
        }
    }

    public static IReadOnlyDictionary<Creature, List<OblivionContribution>> OblivionLedgers => _oblivionLedgers;

    public static async Task AwaitOblivionTaskAsync(Task originalTask)
    {
        try
        {
            StartOblivionExecution();
            await originalTask;
        }
        finally
        {
            _isOblivionExecuting.Value = false;
        }
    }
}
