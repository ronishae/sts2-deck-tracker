using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.ValueProps;

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
        GD.PrintErr($"[DeckTracker] {hookName} failed: {e}");
    }

    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room) => Guard(nameof(AfterRoomEnteredPostfix), () =>
    {
        var currentFloor = ExtractFloorNum(runState);
        var activeDeckIds = ScanDeckForCards(runState);
        GD.Print($"[DeckTracker] AfterRoomEnteredPostfix. Floor: {currentFloor}, Room: {room.RoomType}");
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
        CardRegistry.SaveState();
    });

    public static void BeforeRoomEnteredPrefix(IRunState? runState, AbstractRoom room) => Guard(nameof(BeforeRoomEnteredPrefix), () =>
    {
        var seed = ExtractRunSeed(runState);
        GD.Print($"[DeckTracker] BeforeRoomEnteredPrefix. Seed: {seed}, Room: {room.RoomType}");
        CardRegistry.SyncRun(seed);
    });

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState) => Guard(nameof(BeforeCombatStartPostfix), () =>
    {
        var currentFloor = ExtractFloorNum(runState);
        var currentAct = ExtractActNum(runState);
        var combatType = GetCombatType(runState);
        var activeDeckIds = ScanDeckForCards(runState);

        GD.Print($"[DeckTracker] BeforeCombatStartPostfix. Floor: {currentFloor}, Act: {currentAct}, Type: {combatType}");

        CardRegistry.StartCombat(combatType, currentFloor, currentAct, activeDeckIds);
        CardRegistry.ForcePublish();

        if (!_overlayScheduled)
        {
            _overlayScheduled = true;
            DeckTrackerOverlay.EnsureCreated();
        }
    });

    public static void AfterSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants) => Guard(nameof(AfterSideTurnStartPostfix), () =>
    {
        GD.Print($"[DeckTracker] AfterSideTurnStartPostfix. Side: {side}");
        CardRegistry.ResetOrbTurnState();
    });

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState) => Guard(nameof(AfterCombatEndPostfix), () =>
    {
        GD.Print("[DeckTracker] AfterCombatEndPostfix.");
        CardRegistry.ProcessCombatEnd();
    });

    public static void RunManagerCleanUpPrefix() => Guard(nameof(RunManagerCleanUpPrefix), CardRegistry.ClearSession);

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

    private static List<string> ScanDeckForCards(IRunState? runState)
    {
        List<string> ids = new();
        if (runState == null) return ids;

        for (var i = 0; i < runState.Players.Count; i++)
        {
            var p = runState.Players[i];
            CardRegistry.SetPlayerLabel(i, CardRegistry.GetPlayerDisplayName(p));
            foreach (var c in p.Deck.Cards)
            {
                CardRegistry.RegisterCard(c);
                var id = CardRegistry.GetTrackingId(c);
                CardRegistry.SetCardPlayerIndex(id, i);
                ids.Add(id);
            }
        }
        return ids;
    }
}
