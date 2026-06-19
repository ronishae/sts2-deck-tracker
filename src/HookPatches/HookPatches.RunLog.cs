using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

internal static partial class HookPatches
{
    public static void AfterDamageReceivedPostfix(Creature target, DamageResult result) => Guard(nameof(AfterDamageReceivedPostfix), () =>
    {
        if (!target.IsPlayer)
        {
            return;
        }
        RunLogRecorder.AddDamageTaken(result.UnblockedDamage);
    });

    public static void AfterPlayerTurnStartPostfix() => Guard(nameof(AfterPlayerTurnStartPostfix), RunLogRecorder.IncrementTurn);

    public static void AfterDeathPostfix(IRunState runState, Creature creature, bool wasRemovalPrevented) => Guard(nameof(AfterDeathPostfix), () =>
    {
        // Only a genuine player death ends the run; enemy deaths and prevented removals (Osty revives) are ignored.
        if (!creature.IsPlayer || wasRemovalPrevented)
        {
            return;
        }

        // The Architect (final boss) victory triggers GuaranteeKillAllPlayers after the run is won, so the
        // player dies even though the run is a victory. Detect this via IsVictoryRoom and skip finalization.
        if (runState.CurrentRoom?.IsVictoryRoom == true)
        {
            Log.Info($"AfterDeathPostfix (Victory). Architect victory forced death. Floor: {ExtractFloorNum(runState)}");
            return;
        }

        // In co-op the run continues while any teammate is alive, so only finalize once everyone is down.
        if (runState.Players.Any(p => p.Creature.IsAlive))
        {
            return;
        }
        Log.Info($"AfterDeathPostfix. Player died. Floor: {ExtractFloorNum(runState)}");
        CardRegistry.FinalizeFatalCombat();
    });

    // Opens a combat record with the encounter id and the local player's pre-fight context.
    private static void RecordCombatStartForLog(IRunState? runState)
    {
        if (runState == null)
        {
            return;
        }
        var encounterId = (runState.CurrentRoom as CombatRoom)?.Encounter.Id.Entry ?? "";
        RunLogRecorder.StartCombat(
            ExtractFloorNum(runState), ExtractActNum(runState), ExtractActName(runState), GetCombatType(runState), encounterId);
    }

    // The current act variant id (e.g. OVERGROWTH / UNDERDOCKS), distinct from the 1-based act number.
    private static string ExtractActName(IRunState runState) => runState.Act?.Id.Entry ?? "";
}
