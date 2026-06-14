using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void BeforePowerAmountChangedPostfix(ICombatState combatState, PowerModel power, Decimal amount, Creature target, Creature? applier, CardModel? cardSource) => Guard(nameof(BeforePowerAmountChangedPostfix), () =>
    {
        var powerId = power.Id.Entry ?? "";
        Log.Debug($"BeforePowerAmountChangedPostfix. Power: {powerId}, Amount: {amount}, Target: {target.Name}");

        if (CardRegistry.SimpleDamageTrackers.TryGetValue(powerId, out var simple))
        {
            if (amount > 0 && target.IsPlayer)
            {
                simple.LogApply(cardSource, amount, CardRegistry.GetCurrentSourceId());
            }
            return;
        }

        if (CardRegistry.TargetedTrackers.TryGetValue(powerId, out var targeted))
        {
            if (amount > 0)
            {
                targeted.LogApply(target, cardSource, amount);
            }
            return;
        }

        if (CardRegistry.ProportionalTrackers.TryGetValue(powerId, out var prop))
        {
            var tid = CardRegistry.GetCurrentSourceId(cardSource);
            if (amount > 0)
            {
                prop.AddShares(amount, tid);
            }
            else if (amount < 0)
            {
                prop.RemoveSharesProportionally(Math.Abs(amount));
            }
            return;
        }

        if (CardRegistry.QueueTrackers.TryGetValue(powerId, out var queue))
        {
            if (amount > 0)
            {
                if (powerId == "LIGHTNING_ROD_POWER" || powerId == "SPINNER_POWER")
                {
                    queue.AddDirectCharges(CardRegistry.GetCurrentSourceId(cardSource), amount);
                }
                else
                {
                    queue.LogApply(cardSource, amount);
                }
            }
            return;
        }

        if (powerId == "ROLLING_BOULDER_POWER" || powerId == "PANACHE_POWER" || powerId == "MONOLOGUE_POWER")
        {
             CardRegistry.InstancedTracker.LogInstance(power, cardSource, CardRegistry.GetCurrentSourceId());
        }

        if (CardRegistry.PersistentBuffPowerIds.Contains(powerId) && target.IsPlayer)
        {
            if (amount > 0) CardRegistry.AddPersistentBuff(powerId, amount, cardSource);
            else if (amount < 0) CardRegistry.RemovePersistentBuff(powerId, Math.Abs(amount));
            return;
        }

        if (CardRegistry.DurationDebuffPowerIds.Contains(powerId))
        {
            if (amount > 0) CardRegistry.AddDurationBuff(target, powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
            else if (amount < 0) CardRegistry.RemoveDurationBuff(target, powerId, Math.Abs(amount));
            return;
        }

        switch (power)
        {
            case ConquerorPower:
                CardRegistry.UpdateConquerorTracker(target, amount, cardSource);
                break;
            case SwordSagePower:
                CardRegistry.UpdateSovereignBladeReplayModifierTracker(amount, cardSource);
                break;
            case FurnacePower:
                CardRegistry.UpdateFurnaceHistory(amount, cardSource);
                break;
            case ReaperFormPower:
                if (amount > 0)
                {
                    CardRegistry.AddReaperFormShares(amount, cardSource);
                }
                break;
            case PoisonPower:
                CardRegistry.RoutePoisonApplication(target, amount, cardSource);
                break;
            case CountdownPower:
                if (amount > 0)
                {
                    CardRegistry.AddCountdownHistory(amount, cardSource);
                }
                break;
            case DoomPower:
                CardRegistry.RouteDoomApplication(target, amount, cardSource);
                break;
            case FocusPower:
                CardRegistry.LogFocusChangeById(CardRegistry.GetCurrentSourceId(cardSource), amount);
                break;
            case LoopPower:
                if (amount > 0)
                {
                    CardRegistry.AddLoop(amount, cardSource);
                }
                break;
            case ReflectPower:
                if (amount > 0 && cardSource != null)
                {
                    for (int i = 0; i < amount; i++)
                    {
                        CardRegistry.AddReflect(CardRegistry.GetTrackingId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    for (int i = 0; i > amount; i--)
                    {
                        CardRegistry.DecrementReflect();
                    }
                }
                break;
            case StrengthPower:
                CardRegistry.RouteStrengthApplication(target, powerId, amount, cardSource);
                break;
            case RitualPower:
                if (amount > 0 && target.IsPlayer)
                {
                    var sid = CardRegistry.GetCurrentSourceId(cardSource);
                    if (!string.IsNullOrEmpty(sid))
                    {
                        if (!CardRegistry.RitualSources.ContainsKey(sid))
                        {
                            CardRegistry.RitualSources[sid] = 0;
                        }
                        CardRegistry.RitualSources[sid] += amount;
                        Log.Debug($"BeforePowerAmountChangedPostfix. Ritual Log: {amount} from {sid}");
                    }
                }
                break;
            case VigorPower:
                if (amount > 0)
                {
                    if (CardRegistry.HandoffTrackers["PREP_TIME_POWER"].IsExecuting)
                    {
                        CardRegistry.HandoffTrackers["PREP_TIME_POWER"].ProcessHandoff(powerId, amount);
                    }
                    else
                    {
                        CardRegistry.AddConsumableBuffById(powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemoveConsumableBuff(powerId, Math.Abs(amount));
                }
                break;
            case DoubleDamagePower:
                if (amount > 0)
                {
                    if (CardRegistry.HandoffTrackers["SHADOW_STEP_POWER"].IsExecuting)
                    {
                        CardRegistry.HandoffTrackers["SHADOW_STEP_POWER"].ProcessHandoff(powerId, amount);
                    }
                    else
                    {
                        CardRegistry.AddDurationBuff(target, powerId, amount, CardRegistry.GetCurrentSourceId(cardSource));
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemoveDurationBuff(target, powerId, Math.Abs(amount));
                }
                break;
            case TrackingPower:
                if (amount > 0)
                {
                    var isFirst = !CardRegistry.PersistentLedgers.ContainsKey(powerId) || CardRegistry.PersistentLedgers[powerId].Count == 0;
                    var logged = isFirst ? amount - 1 : amount;
                    if (logged > 0)
                    {
                        CardRegistry.AddPersistentBuff(powerId, logged, cardSource);
                    }
                }
                else if (amount < 0)
                {
                    CardRegistry.RemovePersistentBuff(powerId, Math.Abs(amount));
                }
                break;
        }
    });

    public static void BeforePowerRemovedPrefix(PowerModel? power) => Guard(nameof(BeforePowerRemovedPrefix), () =>
    {
        if (power == null)
        {
            return;
        }
        Log.VeryDebug($"BeforePowerRemovedPrefix. Power: {power.Id.Entry}");
        if (CardRegistry.SimpleDamageTrackers.TryGetValue(power.Id.Entry, out var tracker))
        {
            tracker.Reset();
        }
    });

    public static void GenericPowerPrefix(PowerModel __instance) => Guard(nameof(GenericPowerPrefix), () =>
    {
        if (CardRegistry.SimpleDamageTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            Log.VeryDebug($"GenericPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    });

    public static void GenericPowerPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            if (CardRegistry.SimpleDamageTrackers.TryGetValue(__instance.Id.Entry, out var t))
            {
                __result = t.AwaitTaskAsync(__result);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(GenericPowerPostfix), e);
        }
    }

    public static void TargetedPowerPrefix(PowerModel __instance) => Guard(nameof(TargetedPowerPrefix), () =>
    {
        if (CardRegistry.TargetedTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            Log.VeryDebug($"TargetedPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    });

    public static void TargetedPowerPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            if (CardRegistry.TargetedTrackers.TryGetValue(__instance.Id.Entry, out var t))
            {
                __result = t.AwaitTaskAsync(__result);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(TargetedPowerPostfix), e);
        }
    }

    public static void HandoffPowerPrefix(PowerModel __instance) => Guard(nameof(HandoffPowerPrefix), () =>
    {
        if (CardRegistry.HandoffTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            Log.VeryDebug($"HandoffPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    });

    public static void HandoffPowerPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            if (CardRegistry.HandoffTrackers.TryGetValue(__instance.Id.Entry, out var t))
            {
                __result = t.AwaitTaskAsync(__result);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(HandoffPowerPostfix), e);
        }
    }

    public static void ProportionalPowerPrefix(PowerModel __instance) => Guard(nameof(ProportionalPowerPrefix), () =>
    {
        if (CardRegistry.ProportionalTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            Log.VeryDebug($"ProportionalPowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution();
        }
    });

    public static void ProportionalPowerPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            if (CardRegistry.ProportionalTrackers.TryGetValue(__instance.Id.Entry, out var t))
            {
                __result = t.AwaitTaskAsync(__result);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(ProportionalPowerPostfix), e);
        }
    }

    public static void QueuePowerPrefix(PowerModel __instance) => Guard(nameof(QueuePowerPrefix), () =>
    {
        if (CardRegistry.QueueTrackers.TryGetValue(__instance.Id.Entry, out var t))
        {
            Log.VeryDebug($"QueuePowerPrefix. Power: {__instance.Id.Entry}");
            t.StartExecution(flatten: t.NeedsFlattening);
        }
    });

    public static void QueuePowerPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            if (CardRegistry.QueueTrackers.TryGetValue(__instance.Id.Entry, out var t))
            {
                __result = t.AwaitTaskAsync(__result, flatten: t.NeedsFlattening);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(QueuePowerPostfix), e);
        }
    }

    public static void PoisonAfterSideTurnStartPrefix(PoisonPower __instance) => Guard(nameof(PoisonAfterSideTurnStartPrefix), () =>
    {
        if (!__instance.Owner.IsPlayer)
        {
            Log.VeryDebug($"PoisonAfterSideTurnStartPrefix. Target: {__instance.Owner.Name}");
            CardRegistry.CurrentPoisonTarget.Value = __instance.Owner;
        }
    });

    public static void PoisonAfterSideTurnStartPostfix(PoisonPower __instance, ref Task __result)
    {
        try
        {
            if (!__instance.Owner.IsPlayer)
            {
                __result = CardRegistry.AwaitPoisonTaskAsync(__result);
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(PoisonAfterSideTurnStartPostfix), e);
        }
    }

    public static void DoomKillPrefix(IReadOnlyList<Creature> creatures) => Guard(nameof(DoomKillPrefix), () =>
    {
        Log.Debug($"DoomKillPrefix. Count: {creatures.Count}");
        CardRegistry.CapturePendingDoomHp(creatures);
    });

    public static void AfterDiedToDoomPostfix(ICombatState combatState, IReadOnlyList<Creature> creatures) => Guard(nameof(AfterDiedToDoomPostfix), () =>
    {
        Log.Debug($"AfterDiedToDoomPostfix. Count: {creatures.Count}");
        CardRegistry.DistributeDoomDamage(creatures);
    });

    public static void CountdownAfterSideTurnStartPrefix(CountdownPower __instance) => Guard(nameof(CountdownAfterSideTurnStartPrefix), () =>
    {
        Log.VeryDebug("CountdownAfterSideTurnStartPrefix.");
        CardRegistry.IsCountdownExecuting.Value = true;
    });

    public static void CountdownAfterSideTurnStartPostfix(CountdownPower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.AwaitCountdownTaskAsync(__result);
        }
        catch (Exception e)
        {
            LogHookError(nameof(CountdownAfterSideTurnStartPostfix), e);
        }
    }

    public static void ReaperFormAfterDamageGivenPrefix(ReaperFormPower __instance, DamageResult result) => Guard(nameof(ReaperFormAfterDamageGivenPrefix), () =>
    {
        Log.VeryDebug($"ReaperFormAfterDamageGivenPrefix. Damage: {result.TotalDamage}");
        CardRegistry.StartReaperFormExecution(result.TotalDamage);
    });

    public static void ReaperFormAfterDamageGivenPostfix(ReaperFormPower __instance, ref Task __result, DamageResult result)
    {
        try
        {
            __result = CardRegistry.AwaitReaperFormTaskAsync(__result, result.TotalDamage);
        }
        catch (Exception e)
        {
            LogHookError(nameof(ReaperFormAfterDamageGivenPostfix), e);
        }
    }

    public static void NecroMasteryAfterCurrentHpChangedPrefix(NecroMasteryPower __instance, decimal delta) => Guard(nameof(NecroMasteryAfterCurrentHpChangedPrefix), () =>
    {
        Log.VeryDebug($"NecroMasteryAfterCurrentHpChangedPrefix. Delta: {delta}");
        CardRegistry.StartNecroMasteryExecution(delta);
    });

    public static void NecroMasteryAfterCurrentHpChangedPostfix(NecroMasteryPower __instance, ref Task __result, decimal delta)
    {
        try
        {
            __result = CardRegistry.AwaitNecroMasteryTaskAsync(__result, delta);
        }
        catch (Exception e)
        {
            LogHookError(nameof(NecroMasteryAfterCurrentHpChangedPostfix), e);
        }
    }

    public static void RollingBoulderAfterPlayerTurnStartPrefix(RollingBoulderPower __instance) => Guard(nameof(RollingBoulderAfterPlayerTurnStartPrefix), () =>
    {
        Log.VeryDebug("RollingBoulderAfterPlayerTurnStartPrefix.");
        CardRegistry.InstancedTracker.StartExecution(__instance);
    });

    public static void RollingBoulderAfterPlayerTurnStartPostfix(RollingBoulderPower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.InstancedTracker.AwaitTaskAsync(__result, __instance);
        }
        catch (Exception e)
        {
            LogHookError(nameof(RollingBoulderAfterPlayerTurnStartPostfix), e);
        }
    }

    public static void PrepTimePrefix(PrepTimePower __instance) => Guard(nameof(PrepTimePrefix), () =>
    {
        Log.VeryDebug("PrepTimePrefix.");
        CardRegistry.HandoffTrackers["PREP_TIME_POWER"].StartExecution();
    });

    public static void PrepTimePostfix(PrepTimePower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.HandoffTrackers["PREP_TIME_POWER"].AwaitTaskAsync(__result);
        }
        catch (Exception e)
        {
            LogHookError(nameof(PrepTimePostfix), e);
        }
    }
}
