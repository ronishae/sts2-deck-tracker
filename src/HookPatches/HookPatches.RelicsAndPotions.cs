using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void RelicAfterObtainedPrefix(RelicModel __instance) => Guard(nameof(RelicAfterObtainedPrefix), () =>
    {
        var relicName = __instance.Title.GetFormattedText();
        CardRegistry.RelicNameCache[__instance.Id.Entry] = relicName;

        // Scan the live run state to find which player now owns this relic instance.
        // AfterObtained fires after the relic is added to the player's collection, so the scan succeeds.
        var run = CardRegistry.GetLiveRunState();
        var ownerNetId = run?.Players
            .FirstOrDefault(p => p.Relics.Any(r => ReferenceEquals(r, __instance)))
            ?.NetId.ToString();

        var stats = CardRegistry.GetOrCreateRelicStats(__instance.Id.Entry, ownerNetId);
        if (ownerNetId != null)
        {
            CardRegistry.SetRelicOwnerNetId(__instance, ownerNetId);
            if (CardRegistry.TryGetPlayerIndex(ownerNetId, out var playerIdx))
            {
                stats.PlayerIndex = playerIdx;
            }
        }
        else
        {
            Log.Warn($"RelicAfterObtainedPrefix. Could not find owner for relic: {__instance.Id.Entry}. Using bare key; lazy resolution will migrate on first gameplay event.");
        }

        stats.FloorAdded = __instance.FloorAddedToDeck;
        stats.IsActive = true;
        Log.Debug($"RelicAfterObtainedPrefix. Relic: {__instance.Id.Entry}, OwnerNetId: {ownerNetId}, Floor: {stats.FloorAdded}");
    });

    public static void PlayerRemoveRelicPostfix(Player __instance, RelicModel relic) => Guard(nameof(PlayerRemoveRelicPostfix), () =>
    {
        if (relic != null)
        {
            Log.Debug($"PlayerRemoveRelicPostfix. Relic: {relic.Id.Entry}, Player: {__instance.NetId}");
            CardRegistry.HandleRelicRemove(relic, __instance.NetId.ToString(), ExtractFloorNum(__instance.RunState));
        }
    });

    public static void AfterPotionProcuredPrefix(PotionModel potion) => Guard(nameof(AfterPotionProcuredPrefix), () =>
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        Log.Debug($"AfterPotionProcuredPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.RegisterPotionProcured(potion, floor);
    });

    public static void AfterPotionDiscardedPrefix(PotionModel potion) => Guard(nameof(AfterPotionDiscardedPrefix), () =>
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        Log.Debug($"AfterPotionDiscardedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        CardRegistry.MarkPotionDiscarded(potion, floor);
    });

    public static void BeforePotionUsedPrefix(PotionModel potion) => Guard(nameof(BeforePotionUsedPrefix), () =>
    {
        var floor = CardRegistry.GetLiveRunState()?.TotalFloor ?? 0;
        Log.Debug($"BeforePotionUsedPrefix. Potion: {potion.Id.Entry}, Floor: {floor}");
        var resolvedId = CardRegistry.MarkPotionUsed(potion, floor);
        CardRegistry.SetPlayingPotion(potion, resolvedId);
        CardRegistry.StartPotionUse();
    });

    public static void AfterPotionUsedPrefix(PotionModel potion) => Guard(nameof(AfterPotionUsedPrefix), () =>
    {
        Log.Debug($"AfterPotionUsedPrefix. Potion: {potion.Id.Entry}");
        // Register any cards the potion generated now that its effect (including upgrades, e.g. Cunning
        // Potion) has fully resolved, so they record at their final identity. Done before clearing the
        // potion context so the generated cards still attribute to it.
        CardRegistry.EndPotionUse();
        CardRegistry.SetPlayingPotion(null);
    });

    public static void RitualPowerTurnEndPrefix() => Guard(nameof(RitualPowerTurnEndPrefix), () =>
    {
        Log.VeryDebug("RitualPowerTurnEndPrefix.");
        CardRegistry.IsRitualTriggering = true;
    });

    public static void RitualPowerTurnEndPostfix() => Guard(nameof(RitualPowerTurnEndPostfix), () =>
    {
        CardRegistry.IsRitualTriggering = false;
    });

    public static void HandDrillAfterDamagePrefix(RelicModel __instance) => Guard(nameof(HandDrillAfterDamagePrefix), () =>
    {
        RelicExecutionManager.ExecutingRelicId = CardRegistry.GetRelicScopedId(__instance);
    });

    public static void HandDrillAfterDamagePostfix(RelicModel __instance) => Guard(nameof(HandDrillAfterDamagePostfix), () =>
    {
        RelicExecutionManager.ExecutingRelicId = null;
    });

    // Has a ref parameter, which cannot be captured by the Guard lambda, so it is isolated inline instead.
    public static void TheBootModifyHpPostfix(MegaCrit.Sts2.Core.Models.Relics.TheBoot __instance, decimal amount, ref decimal __result)
    {
        try
        {
            if (__result > amount)
            {
                var floor = (int)Math.Floor(amount);
                var boot = (int)Math.Floor(__result - floor);
                Log.Debug($"TheBootModifyHpPostfix. Damage: {boot}");
                CardRegistry.AddDamageById(CardRegistry.GetRelicLedgerKey(__instance), boot);
                CardRegistry.PendingBootDamage += boot;
            }
        }
        catch (Exception e)
        {
            LogHookError(nameof(TheBootModifyHpPostfix), e);
        }
    }
}
