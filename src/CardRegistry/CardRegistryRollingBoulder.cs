using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static ConditionalWeakTable<PowerModel, string> _boulderInstanceMap = new();
    private static RollingBoulderPower? _executingBoulder;

    public static RollingBoulderPower? ExecutingBoulder => _executingBoulder;

    public static void ResetRollingBoulderState()
    {
        lock (SyncRoot)
        {
            _boulderInstanceMap = new ConditionalWeakTable<PowerModel, string>();
            _executingBoulder = null;
        }
    }

    public static void LogRollingBoulderInstance(PowerModel power, CardModel? cardSource)
    {
        if (cardSource == null) return;

        lock (SyncRoot)
        {
            var trackingId = GetTrackingId(cardSource);
            if (!_boulderInstanceMap.TryGetValue(power, out _))
            {
                _boulderInstanceMap.Add(power, trackingId);
                GD.Print($"[DeckTracker] LogRollingBoulderInstance. Power instance {power.GetHashCode()} mapped to {trackingId}");
            }
        }
    }

    public static void StartRollingBoulderExecution(RollingBoulderPower power)
    {
        _executingBoulder = power;
        GD.Print($"[DeckTracker] StartRollingBoulderExecution. Instance: {power.GetHashCode()}");
    }

    public static void DistributeRollingBoulderDamage(decimal totalDamage)
    {
        if (totalDamage <= 0 || _executingBoulder == null) return;

        lock (SyncRoot)
        {
            if (_boulderInstanceMap.TryGetValue(_executingBoulder, out var trackingId))
            {
                AddDamageById(trackingId, totalDamage);
                GD.Print($"[DeckTracker] DistributeRollingBoulderDamage. Attributed {totalDamage} to {trackingId} (Instance: {_executingBoulder.GetHashCode()})");
            }
            else
            {
                GD.Print($"[DeckTracker] DistributeRollingBoulderDamage. No mapping found for boulder instance {_executingBoulder.GetHashCode()}");
            }
        }
    }

    public static async Task AwaitRollingBoulderTaskAsync(Task originalTask, RollingBoulderPower power)
    {
        try
        {
            StartRollingBoulderExecution(power);
            await originalTask;
        }
        finally
        {
            _executingBoulder = null;
            GD.Print($"[DeckTracker] AwaitRollingBoulderTaskAsync finished. Instance: {power.GetHashCode()}");
        }
    }
}
