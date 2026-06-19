using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void OrbChannelPostfix(PlayerChoiceContext choiceContext, OrbModel orb, Player player) => Guard(nameof(OrbChannelPostfix), () =>
    {
        Log.VeryDebug($"OrbChannelPostfix. Orb: {orb.Id.Entry}");
        CardRegistry.RegisterChanneledOrb(orb, CardRegistry.CurrentPlayingCard);
    });

    public static void GoldPlatedCablesModifyOrbPassivePostfix(GoldPlatedCables __instance, int triggerCount, int __result)
        => Guard(nameof(GoldPlatedCablesModifyOrbPassivePostfix), () =>
    {
        Log.VeryDebug($"GoldPlatedCablesModifyOrbPassivePostfix. TriggerDelta: {__result - triggerCount}");
        if (__result > triggerCount)
        {
            CardRegistry.PendingExtraPassiveSource = __instance;
        }
    });

    public static void OrbPassivePrefix(OrbModel __instance) => Guard(nameof(OrbPassivePrefix), () =>
    {
        string? forcingActor = null;
        if (CardRegistry.IsLoopExecuting.Value && CardRegistry.CurrentTurnLoopQueue.Count > 0)
        {
            forcingActor = CardRegistry.CurrentTurnLoopQueue[0];
            CardRegistry.CurrentTurnLoopQueue.RemoveAt(0);
        }
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId))
        {
            forcingActor = "RELIC_" + RelicExecutionManager.ExecutingRelicId;
        }
        else if (CardRegistry.CurrentPlayingCard != null)
        {
            forcingActor = CardRegistry.GetTrackingId(CardRegistry.CurrentPlayingCard);
        }
        else
        {
            lock (CardRegistry.SyncRoot)
            {
                int count = CardRegistry.EotPassiveCounts.GetValueOrDefault(__instance, 0) + 1;
                CardRegistry.EotPassiveCounts[__instance] = count;
                if (count > 1 && CardRegistry.PendingExtraPassiveSource != null)
                {
                    forcingActor = CardRegistry.GetRelicLedgerKey(CardRegistry.PendingExtraPassiveSource);
                    CardRegistry.PendingExtraPassiveSource = null;
                }
            }
        }
        Log.VeryDebug($"OrbPassivePrefix. Orb: {__instance.Id.Entry}, ForcingActor: {forcingActor}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, false, __instance.PassiveVal, forcingActor);
    });

    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        try
        {
            Log.VeryDebug($"OrbPassivePostfix. Orb: {__instance.Id.Entry}");
            __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
        }
        catch (Exception e)
        {
            LogHookError(nameof(OrbPassivePostfix), e);
        }
    }

    public static void OrbEvokePrefix(OrbModel __instance) => Guard(nameof(OrbEvokePrefix), () =>
    {
        Log.VeryDebug($"OrbEvokePrefix. Orb: {__instance.Id.Entry}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, true, __instance.EvokeVal);
    });

    public static void OrbEvokePostfix(OrbModel __instance, ref Task<IEnumerable<Creature>> __result)
    {
        try
        {
            Log.VeryDebug($"OrbEvokePostfix. Orb: {__instance.Id.Entry}");
            __result = CardRegistry.AwaitOrbEvokeTaskAsync(__result, __instance);
        }
        catch (Exception e)
        {
            LogHookError(nameof(OrbEvokePostfix), e);
        }
    }

    public static void TempFocusApplyPrefix(TemporaryFocusPower __instance) => Guard(nameof(TempFocusApplyPrefix), () =>
    {
        Log.VeryDebug("TempFocusApplyPrefix.");
        CardRegistry.IsApplyingTemporaryFocus.Value = true;
    });

    public static void TempFocusApplyPostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.AwaitTempFocusApplyAsync(__result);
        }
        catch (Exception e)
        {
            LogHookError(nameof(TempFocusApplyPostfix), e);
        }
    }

    public static void TempFocusExpirePrefix(TemporaryFocusPower __instance) => Guard(nameof(TempFocusExpirePrefix), () =>
    {
        Log.VeryDebug("TempFocusExpirePrefix.");
        CardRegistry.IsExpiringTemporaryFocus.Value = true;
    });

    public static void TempFocusExpirePostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.AwaitTempFocusExpireAsync(__result);
        }
        catch (Exception e)
        {
            LogHookError(nameof(TempFocusExpirePostfix), e);
        }
    }

    public static void LoopPrefix(LoopPower __instance) => Guard(nameof(LoopPrefix), () =>
    {
        Log.VeryDebug("LoopPrefix.");
        CardRegistry.IsLoopExecuting.Value = true;
        CardRegistry.CurrentTurnLoopQueue.Clear();
        lock (CardRegistry.SyncRoot)
        {
            foreach (var contribution in CardRegistry.LoopHistory)
            {
                for (int i = 0; i < contribution.Amount; i++)
                {
                    CardRegistry.CurrentTurnLoopQueue.Add(contribution.TrackingId);
                }
            }
        }
    });

    public static void LoopPostfix(LoopPower __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.AwaitLoopTaskAsync(__result);
        }
        catch (Exception e)
        {
            LogHookError(nameof(LoopPostfix), e);
        }
    }
}
