using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

internal static partial class HookPatches
{
    private static bool _overlayScheduled;

    // Runs a hook body in a guarded context. This is a read-only tracking mod, but its hooks fire
    // inside the game's synchronized simulation during co-op — an exception escaping a hook would
    // abort the synchronized work on this client only and desync the run from the host. Guarding
    // every hook means the mod can fail to record an event, but can never crash or desync the game.
    private static void Guard(string hookName, Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            LogHookError(hookName, e);
        }
    }

    private static void LogHookError(string hookName, Exception e)
    {
        Log.Error($"{hookName} failed: {e}");
    }

    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room) => Guard(nameof(AfterRoomEnteredPostfix), () =>
    {
        var currentFloor = ExtractFloorNum(runState);
        var activeDeckIds = ScanDeckForCards(runState);
        Log.Info($"AfterRoomEnteredPostfix. Floor: {currentFloor}, Room: {room.RoomType}");
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
        RecordRoomForLog(runState, room);
        RecordDeckSyncForLog(runState, room);
        CardRegistry.SaveState();
    });

    public static void BeforeRoomEnteredPrefix(IRunState? runState, AbstractRoom room) => Guard(nameof(BeforeRoomEnteredPrefix), () =>
    {
        var seed = ExtractRunSeed(runState);
        Log.Info($"BeforeRoomEnteredPrefix. Seed: {seed}, Room: {room.RoomType}");
        CardRegistry.SyncRun(seed);
    });

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState) => Guard(nameof(BeforeCombatStartPostfix), () =>
    {
        var currentFloor = ExtractFloorNum(runState);
        var currentAct = ExtractActNum(runState);
        var combatType = GetCombatType(runState);
        var activeDeckIds = ScanDeckForCards(runState);

        Log.Info($"BeforeCombatStartPostfix. Floor: {currentFloor}, Act: {currentAct}, Type: {combatType}");

        CardRegistry.StartCombat(combatType, currentFloor, currentAct, activeDeckIds);
        RecordCombatStartForLog(runState);
        CardRegistry.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    });

    public static void AfterSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants) => Guard(nameof(AfterSideTurnStartPostfix), () =>
    {
        Log.Debug($"AfterSideTurnStartPostfix. Side: {side}");
        CardRegistry.ResetOrbTurnState();
    });

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState) => Guard(nameof(AfterCombatEndPostfix), () =>
    {
        Log.Info("AfterCombatEndPostfix.");
        CardRegistry.ProcessCombatEnd();
    });

    public static void RunManagerCleanUpPrefix(RunManager __instance) => Guard(nameof(RunManagerCleanUpPrefix), () =>
    {
        // A player-initiated abandon force-kills the players, so the run was already (wrongly) recorded as a
        // death; relabel it as abandoned before the finalize export overwrites the JSON.
        if (__instance.IsAbandoned)
        {
            RunLogRecorder.MarkAbandoned();
        }
        FinalizeRunForLog();
        CardRegistry.ClearSession();
    });

    // Fires only for a brand-new run (not a load/resume), so a same-seed restart wipes the prior run's data.
    public static void SetUpNewRunPostfix() => Guard(nameof(SetUpNewRunPostfix), CardRegistry.BeginNewRun);

    private static int ExtractFloorNum(IRunState? runState) => runState?.TotalFloor ?? 1;
    private static int ExtractActNum(IRunState? runState) => (runState?.CurrentActIndex ?? 0) + 1;

    private static string GetCombatType(IRunState? runState)
    {
        if (runState != null)
        {
            try
            {
                var t = runState.BaseRoom?.RoomType;
                if (t == RoomType.Monster) return "Hallway";
                if (t == RoomType.Elite) return "Elite";
                if (t == RoomType.Boss) return "Boss";
            }
            catch { }
        }
        return "Hallway";
    }

    private static string ExtractRunSeed(IRunState? runState)
    {
        if (runState == null) return "";
        try
        {
            var r = runState.GetType().GetProperty("Rng")?.GetValue(runState);
            return r?.GetType().GetProperty("StringSeed")?.GetValue(r)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static int _lastDeckSignature;

    // Re-scans + republishes the deck the moment its composition changes out of combat, so card
    // add/remove/upgrade/enchant show instantly instead of waiting for the next combat. Gated to
    // out-of-combat: the rescan's BeginDeckScan resets copy-index maps that live card tracking relies on
    // during combat.
    public static void PollDeckChange() => Guard(nameof(PollDeckChange), () =>
    {
        var run = CardRegistry.GetLiveRunState();
        if (run == null || CardRegistry.IsCombatActive)
        {
            return;
        }
        var signature = ComputeDeckSignature(run);
        if (signature == _lastDeckSignature)
        {
            return;
        }
        _lastDeckSignature = signature;
        Log.Debug($"PollDeckChange. Deck changed (sig {signature}); resyncing.");
        CardRegistry.SyncDeckState(ExtractFloorNum(run), ScanDeckForCards(run));
        RecordDeckSyncForLog(run, run.CurrentRoom);
    });

    // Order-independent signature over every player's master deck: changes on add/remove (count + members),
    // upgrade (CurrentUpgradeLevel) and enchant (Enchantment).
    private static int ComputeDeckSignature(IRunState run)
    {
        long sum = 0;
        var count = 0;
        foreach (var p in run.Players)
        {
            foreach (var c in p.Deck.Cards)
            {
                sum += HashCode.Combine(c.Id.Entry, c.CurrentUpgradeLevel, c.Enchantment?.Id.Entry ?? "", p.NetId);
                count++;
            }
        }
        return HashCode.Combine(sum, count);
    }

    private static List<string> ScanDeckForCards(IRunState? runState)
    {
        List<string> ids = new();
        if (runState == null) return ids;

        CardRegistry.BeginDeckScan();
        for (var i = 0; i < runState.Players.Count; i++)
        {
            var p = runState.Players[i];
            var netId = p.NetId.ToString();
            CardRegistry.SetPlayerIndexForNetId(netId, i);
            CardRegistry.SetPlayerLabel(i, CardRegistry.GetPlayerDisplayName(p));
            foreach (var c in p.Deck.Cards)
            {
                CardRegistry.RegisterCard(c, netId, isDeckScan: true);
                var id = CardRegistry.GetTrackingId(c, netId);
                ids.Add(id);
            }
        }
        return ids;
    }
}
