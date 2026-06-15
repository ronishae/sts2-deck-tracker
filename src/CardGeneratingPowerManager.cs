using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

// Wires up powers that create cards (e.g. Infinite Blades, Spectrum Shift) so the cards they generate
// attribute back to the card that applied the power. Mirrors RelicExecutionManager's list-driven setup:
// to support a new card-generating power, add a single line to Hooks.
public static class CardGeneratingPowerManager
{
    // EDIT HERE to support a new card-generating power: (power type, the method that creates the cards).
    private static readonly (Type PowerType, string Method)[] Hooks =
    {
        (typeof(InfiniteBladesPower), nameof(InfiniteBladesPower.BeforeHandDraw)),
        (typeof(SpectrumShiftPower), nameof(SpectrumShiftPower.BeforeHandDraw)),
    };

    // The power types above. BeforePowerAmountChanged uses this to map each applied instance to the card
    // that applied it, so the generation wrap below can resolve the creator.
    public static readonly HashSet<Type> PowerTypes = Hooks.Select(hook => hook.PowerType).ToHashSet();

    public static void PatchAll(Harmony harmony)
    {
        var prefix = new HarmonyMethod(AccessTools.Method(typeof(CardGeneratingPowerManager), nameof(GenerationPrefix)));
        var postfix = new HarmonyMethod(AccessTools.Method(typeof(CardGeneratingPowerManager), nameof(GenerationPostfix)));

        foreach (var (powerType, method) in Hooks)
        {
            var original = AccessTools.Method(powerType, method);
            if (original == null)
            {
                Log.Warn($"CardGeneratingPowerManager. Method not found: {powerType.Name}.{method}");
                continue;
            }
            harmony.Patch(original, prefix: prefix, postfix: postfix);
            Log.Debug($"PatchAll. Patched card-generating power: {powerType.Name}.{method}");
        }
    }

    // While the power creates cards, point the executing source at the card that applied it (resolved via
    // the instance mapping logged on apply) so the generated cards attribute back to it. Plain, no Guard,
    // matching RelicExecutionManager.GenericRelicPrefix/Postfix.
    public static void GenerationPrefix(PowerModel __instance)
    {
        CardRegistry.InstancedTracker.StartExecution(__instance);
    }

    public static void GenerationPostfix(PowerModel __instance, ref Task __result)
    {
        try
        {
            __result = CardRegistry.InstancedTracker.AwaitTaskAsync(__result, __instance);
        }
        catch (Exception e)
        {
            Log.Error($"CardGeneratingPowerManager.GenerationPostfix. Power: {__instance.Id.Entry}, Error: {e}");
        }
    }
}
