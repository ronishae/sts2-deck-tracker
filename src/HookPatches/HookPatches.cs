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

    public static void AfterRoomEnteredPostfix(IRunState runState, AbstractRoom room)
    {
        var currentFloor = ExtractFloorNum(runState);
        var activeDeckIds = ScanDeckForCards(runState);
        GD.Print($"[DeckTracker] AfterRoomEnteredPostfix. Floor: {currentFloor}, Room: {room.RoomType}");
        CardRegistry.SyncDeckState(currentFloor, activeDeckIds);
        CardRegistry.SaveState();
    }

    public static void BeforeRoomEnteredPrefix(IRunState? runState, AbstractRoom room)
    {
        var seed = ExtractRunSeed(runState);
        GD.Print($"[DeckTracker] BeforeRoomEnteredPrefix. Seed: {seed}, Room: {room.RoomType}");
        CardRegistry.SyncRun(seed);
    }

    public static void BeforeCombatStartPostfix(IRunState? runState, CombatState? combatState)
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
    }

    public static void AfterSideTurnStartPostfix(ICombatState combatState, CombatSide side, IReadOnlyList<Creature> participants)
    {
        GD.Print($"[DeckTracker] AfterSideTurnStartPostfix. Side: {side}");
        CardRegistry.ResetOrbTurnState();
    }

    public static void AfterCombatEndPostfix(IRunState? runState, CombatState? combatState)
    {
        GD.Print("[DeckTracker] AfterCombatEndPostfix.");
        CardRegistry.ProcessCombatEnd();
    }

    public static void RunManagerCleanUpPrefix() => CardRegistry.ClearSession();

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
