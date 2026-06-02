using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void OrbChannelPostfix(PlayerChoiceContext choiceContext, OrbModel orb, Player player)
    {
        GD.Print($"[DeckTracker] OrbChannelPostfix. Orb: {orb.Id.Entry}");
        CardRegistry.RegisterChanneledOrb(orb, CardRegistry.CurrentPlayingCard);
    }

    public static void OrbPassivePrefix(OrbModel __instance)
    {
        string? forcingActor = null;
        if (CardRegistry.IsLoopExecuting.Value && CardRegistry.CurrentTurnLoopQueue.Count > 0)
        {
            forcingActor = CardRegistry.CurrentTurnLoopQueue[0];
            CardRegistry.CurrentTurnLoopQueue.RemoveAt(0);
        }
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            forcingActor = "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
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
                if (count > 1)
                {
                    forcingActor = "RELIC_GoldPlatedCables";
                }
            }
        }
        GD.Print($"[DeckTracker] OrbPassivePrefix. Orb: {__instance.Id.Entry}, ForcingActor: {forcingActor}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, false, __instance.PassiveVal, forcingActor);
    }

    public static void OrbPassivePostfix(OrbModel __instance, ref Task __result)
    {
        GD.Print($"[DeckTracker] OrbPassivePostfix. Orb: {__instance.Id.Entry}");
        __result = CardRegistry.AwaitOrbExecutionTaskAsync(__result, __instance, isEvoke: false);
    }

    public static void OrbEvokePrefix(OrbModel __instance)
    {
        GD.Print($"[DeckTracker] OrbEvokePrefix. Orb: {__instance.Id.Entry}");
        CardRegistry.ExecutingOrb = new OrbExecutionContext(__instance, true, __instance.EvokeVal);
    }

    public static void OrbEvokePostfix(OrbModel __instance, ref Task<IEnumerable<Creature>> __result)
    {
        GD.Print($"[DeckTracker] OrbEvokePostfix. Orb: {__instance.Id.Entry}");
        __result = CardRegistry.AwaitOrbEvokeTaskAsync(__result, __instance);
    }

    public static void TempFocusApplyPrefix(TemporaryFocusPower __instance)
    {
        GD.Print("[DeckTracker] TempFocusApplyPrefix.");
        CardRegistry.IsApplyingTemporaryFocus.Value = true;
    }

    public static void TempFocusApplyPostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusApplyAsync(__result);
    }

    public static void TempFocusExpirePrefix(TemporaryFocusPower __instance)
    {
        GD.Print("[DeckTracker] TempFocusExpirePrefix.");
        CardRegistry.IsExpiringTemporaryFocus.Value = true;
    }

    public static void TempFocusExpirePostfix(TemporaryFocusPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitTempFocusExpireAsync(__result);
    }

    public static void LoopPrefix(LoopPower __instance)
    {
        GD.Print("[DeckTracker] LoopPrefix.");
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
    }

    public static void LoopPostfix(LoopPower __instance, ref Task __result)
    {
        __result = CardRegistry.AwaitLoopTaskAsync(__result);
    }
}
