using System;
using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void RelicAfterObtainedPrefix(RelicModel __instance)
    {
        CardRegistry.RelicNameCache[__instance.Id.Entry] = __instance.Title.GetFormattedText();
        var stats = CardRegistry.GetOrCreateRelicStats(__instance.Id.Entry);
        stats.FloorAdded = __instance.FloorAddedToDeck;
        stats.IsActive = true;
        GD.Print($"[DeckTracker] RelicAfterObtainedPrefix. Relic: {__instance.Id.Entry}, Floor: {stats.FloorAdded}");
    }

    public static void PlayerRemoveRelicPostfix(Player __instance, RelicModel relic)
    {
        if (relic != null)
        {
            GD.Print($"[DeckTracker] PlayerRemoveRelicPostfix. Relic: {relic.Id.Entry}");
            CardRegistry.HandleRelicRemove(relic, ExtractFloorNum(__instance.RunState));
        }
    }

    public static void AfterPotionProcuredPrefix(PotionModel potion)
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] AfterPotionProcuredPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.RegisterPotionProcured(potion, floor);
    }

    public static void AfterPotionDiscardedPrefix(PotionModel potion)
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] AfterPotionDiscardedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.MarkPotionDiscarded(potion, floor);
    }

    public static void BeforePotionUsedPrefix(PotionModel potion)
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        GD.Print($"[DeckTracker] BeforePotionUsedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.MarkPotionUsed(potion, floor);
        CardRegistry.SetPlayingPotion(potion);
    }

    public static void AfterPotionUsedPrefix(PotionModel potion)
    {
        GD.Print($"[DeckTracker] AfterPotionUsedPrefix. Potion: {potion.Id.Entry}");
        CardRegistry.SetPlayingPotion(null);
    }

    public static void RitualPowerTurnEndPrefix()
    {
        GD.Print("[DeckTracker] RitualPowerTurnEndPrefix.");
        CardRegistry.IsRitualTriggering.Value = true;
    }

    public static void RitualPowerTurnEndPostfix()
    {
        CardRegistry.IsRitualTriggering.Value = false;
    }

    public static void HandDrillAfterDamagePrefix()
    {
        RelicExecutionManager.ExecutingRelicId.Value = "HAND_DRILL";
    }

    public static void HandDrillAfterDamagePostfix()
    {
        RelicExecutionManager.ExecutingRelicId.Value = null;
    }

    public static void TheBootModifyHpPostfix(MegaCrit.Sts2.Core.Models.Relics.TheBoot __instance, decimal amount, ref decimal __result)
    {
        if (__result > amount)
        {
            var floor = (int)Math.Floor(amount);
            var boot = (int)Math.Floor(__result - floor);
            GD.Print($"[DeckTracker] TheBootModifyHpPostfix. Damage: {boot}");
            CardRegistry.AddDamageById("RELIC_" + (__instance.Id.Entry ?? "THE_BOOT"), boot);
            CardRegistry.PendingBootDamage.Value += boot;
        }
    }
}
