using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace DeckTracker;

// Hook bodies that feed the run export log (RunLogRecorder). These translate the game's objects into the
// plain primitives the recorder stores. Every body is guarded so an export-tracking failure can never
// crash or desync a run. Harmony injects only the parameters named here, so unused hook arguments
// (choice contexts, value props, etc.) are simply omitted.
internal static partial class HookPatches
{
    public static void AfterMapGeneratedPostfix(IRunState runState, ActMap map, int actIndex) => Guard(nameof(AfterMapGeneratedPostfix), () =>
    {
        // The first act's map is generated before the starting room is entered — i.e. before
        // BeforeRoomEntered creates the run log — so sync the run here too (idempotent) to ensure the log
        // exists. Without this, act 1's map is recorded into a null log and dropped.
        CardRegistry.SyncRun(ExtractRunSeed(runState));
        RunLogRecorder.RecordMap(actIndex, BuildMapNodes(map));
    });

    public static void AfterActEnteredPostfix(IRunState runState) => Guard(nameof(AfterActEnteredPostfix), () =>
    {
        RunLogRecorder.RecordActEntered(ExtractActNum(runState), ExtractFloorNum(runState), ExtractActName(runState));
    });

    public static void AfterDamageReceivedPostfix(Creature target, DamageResult result) => Guard(nameof(AfterDamageReceivedPostfix), () =>
    {
        if (!target.IsPlayer)
        {
            return;
        }
        RunLogRecorder.AddDamageTaken(result.UnblockedDamage);
    });

    public static void AfterBlockGainedPostfix(Creature creature, decimal amount) => Guard(nameof(AfterBlockGainedPostfix), () =>
    {
        if (!creature.IsPlayer)
        {
            return;
        }
        RunLogRecorder.AddBlockGained(amount);
    });

    public static void AfterPlayerTurnStartPostfix() => Guard(nameof(AfterPlayerTurnStartPostfix), RunLogRecorder.IncrementTurn);

    public static void AfterGoldGainedPostfix(IRunState runState, Player player) => Guard(nameof(AfterGoldGainedPostfix), () =>
    {
        RunLogRecorder.RecordGoldGained(ExtractFloorNum(runState), ExtractActNum(runState), player.Gold, GoldRoomLabel(runState.CurrentRoom));
    });

    public static void AfterItemPurchasedPostfix(IRunState runState, Player player, MerchantEntry itemPurchased, int goldSpent) => Guard(nameof(AfterItemPurchasedPostfix), () =>
    {
        RunLogRecorder.RecordPurchase(ExtractFloorNum(runState), ExtractActNum(runState), itemPurchased.GetType().Name, goldSpent);
        // A purchase spends gold without firing AfterGoldGained, so re-sync the baseline to the new total.
        RunLogRecorder.SetGoldBaseline(player.Gold);
    });

    public static void AfterRewardTakenPostfix(IRunState runState, Reward reward) => Guard(nameof(AfterRewardTakenPostfix), () =>
    {
        // Only card rewards represent a meaningful choice to log; gold/relic/potion/removal rewards are
        // already covered by their own timeline events (GoldGained, RelicGained, PotionGained, deck changes).
        if (reward is not CardReward card)
        {
            return;
        }
        var offered = string.Join(", ", card.Cards.Select(c => c.Title ?? c.Id.Entry));
        RunLogRecorder.RecordReward(ExtractFloorNum(runState), ExtractActNum(runState), "Card", offered, reward.SuccessfullySelected);

        // When a card was taken it is already in the deck; diff the deck now so it is logged as a CardAdded
        // (Source Reward) immediately, instead of relying on the next deck poll or room entry. Gated to
        // out-of-combat: ScanDeckForCards resets copy-index maps that live combat tracking depends on.
        if (reward.SuccessfullySelected && !CardRegistry.IsCombatActive)
        {
            ScanDeckForCards(runState);
            RecordDeckSyncForLog(runState, runState.CurrentRoom);
        }
    });

    public static void AfterRestSiteHealPostfix(IRunState runState) => Guard(nameof(AfterRestSiteHealPostfix), () =>
    {
        RunLogRecorder.RecordRest(ExtractFloorNum(runState), ExtractActNum(runState), false);
    });

    public static void AfterRestSiteSmithPostfix(IRunState runState) => Guard(nameof(AfterRestSiteSmithPostfix), () =>
    {
        RunLogRecorder.RecordRest(ExtractFloorNum(runState), ExtractActNum(runState), true);
    });

    public static void AfterDeathPostfix(IRunState runState, Creature creature, bool wasRemovalPrevented) => Guard(nameof(AfterDeathPostfix), () =>
    {
        // Only a genuine player death ends the run; enemy deaths and prevented removals (Osty revives) are ignored.
        if (!creature.IsPlayer || wasRemovalPrevented)
        {
            return;
        }
        // In co-op the run continues while any teammate is alive, so only record the loss once everyone is down.
        if (runState.Players.Any(p => p.Creature.IsAlive))
        {
            return;
        }
        var encounter = (runState.CurrentRoom as CombatRoom)?.Encounter.Id.Entry ?? "";
        var finalGold = runState.Players.Count > 0 ? runState.Players[0].Gold : 0;
        Log.Info($"AfterDeathPostfix. Player died. Floor: {ExtractFloorNum(runState)}, Gold: {finalGold}, Encounter: {encounter}");
        RunLogRecorder.MarkDeath(ExtractFloorNum(runState), finalGold, encounter);
        CardRegistry.FinalizeFatalCombat();
    });

    // Abandoning a run from the main menu (after a save & quit) never loads the run, so RunManager.CleanUp
    // does not fire and the export keeps its stale "InProgress" outcome. The saved run is read off the menu
    // node's already-loaded save result, then its previously exported JSON is reloaded, relabelled, and
    // rewritten in place. Runs as a prefix because AbandonRun ends by refreshing the menu, which clears
    // _readRunSaveResult once the underlying save is deleted.
    public static void MainMenuAbandonRunPrefix(NMainMenu __instance) => Guard(nameof(MainMenuAbandonRunPrefix), () =>
    {
        var saveResult = AccessTools.FieldRefAccess<NMainMenu, ReadSaveResult<SerializableRun>?>("_readRunSaveResult")(__instance);
        if (saveResult is not { Success: true, SaveData: { } saveData })
        {
            Log.Warn("MainMenuAbandonRunPrefix. No valid run save to mark abandoned.");
            return;
        }

        var seed = saveData.SerializableRng?.Seed ?? "";
        Log.Info($"MainMenuAbandonRunPrefix. Abandoning saved run from main menu. Seed: {seed}");
        if (string.IsNullOrEmpty(seed))
        {
            Log.Warn("MainMenuAbandonRunPrefix. Save has no seed; cannot locate export.");
            return;
        }

        var log = RunExporter.TryLoadExportedRun(seed);
        if (log == null)
        {
            Log.Warn($"MainMenuAbandonRunPrefix. No export to amend for seed: {seed}");
            return;
        }
        RunLogRecorder.MarkAbandoned(log);
        RunExporter.ExportRun(log);
    });

    public static void AfterCombatVictoryPostfix(IRunState runState, CombatRoom room) => Guard(nameof(AfterCombatVictoryPostfix), () =>
    {
        // The run is won when the final act's boss is defeated.
        if (room.RoomType == RoomType.Boss && runState.CurrentActIndex >= runState.Acts.Count - 1)
        {
            RunLogRecorder.MarkVictory(ExtractFloorNum(runState));
        }
    });

    // --- Helpers shared with the core lifecycle hooks (HookPatches.cs) ---

    // Records the room entry (path step + timeline event) using the player's current map point when known.
    private static void RecordRoomForLog(IRunState runState, AbstractRoom room)
    {
        var act = ExtractActNum(runState);
        var floor = ExtractFloorNum(runState);
        var point = runState.CurrentMapPoint;
        if (point != null)
        {
            RunLogRecorder.RecordRoomEntered(act, floor, point.coord.col, point.coord.row, point.PointType.ToString());
            return;
        }
        RunLogRecorder.RecordRoomEntered(act, floor, -1, -1, room.RoomType.ToString());
    }

    // Diffs the master deck for the export log's deck-change list, inferring the source from the room type.
    private static void RecordDeckSyncForLog(IRunState runState, AbstractRoom? room)
    {
        RunLogRecorder.SyncDeck(ExtractFloorNum(runState), RoomSource(room), CardRegistry.BuildDeckInfo(runState));
    }

    // Opens a combat record with the encounter id and the local player's pre-fight HP/gold.
    private static void RecordCombatStartForLog(IRunState? runState)
    {
        if (runState == null)
        {
            return;
        }
        var encounterId = (runState.CurrentRoom as CombatRoom)?.Encounter.Id.Entry ?? "";
        var player = runState.Players.Count > 0 ? runState.Players[0] : null;
        RunLogRecorder.StartCombat(
            ExtractFloorNum(runState), ExtractActNum(runState), ExtractActName(runState), GetCombatType(runState), encounterId,
            player?.Creature.CurrentHp ?? 0, player?.Gold ?? 0);
    }

    // The current act variant id (e.g. OVERGROWTH / UNDERDOCKS), distinct from the 1-based act number.
    private static string ExtractActName(IRunState runState) => runState.Act?.Id.Entry ?? "";

    // Writes the run's final floor/gold and the closing JSON snapshot when the run is torn down.
    private static void FinalizeRunForLog()
    {
        var run = CardRegistry.GetLiveRunState();
        var player = run != null && run.Players.Count > 0 ? run.Players[0] : null;
        RunLogRecorder.FinalizeRun(run != null ? ExtractFloorNum(run) : 0, player?.Gold ?? 0);
    }

    private static List<MapNodeSnapshot> BuildMapNodes(ActMap map)
    {
        var byCoord = new Dictionary<(int, int), MapNodeSnapshot>();
        void Add(MapPoint? point)
        {
            if (point == null || byCoord.ContainsKey((point.coord.col, point.coord.row)))
            {
                return;
            }
            byCoord[(point.coord.col, point.coord.row)] = new MapNodeSnapshot
            {
                Col = point.coord.col,
                Row = point.coord.row,
                PointType = point.PointType.ToString(),
                Children = point.Children.Select(c => new[] { c.coord.col, c.coord.row }).ToList()
            };
        }

        foreach (var point in map.GetAllMapPoints())
        {
            Add(point);
        }
        Add(map.StartingMapPoint);
        Add(map.BossMapPoint);
        Add(map.SecondBossMapPoint);
        return byCoord.Values.ToList();
    }

    // Labels where a gold gain came from, for the GoldGained timeline event.
    private static string GoldRoomLabel(AbstractRoom? room)
    {
        return room?.RoomType switch
        {
            RoomType.Monster or RoomType.Elite or RoomType.Boss => "Combat",
            RoomType.Shop => "Shop",
            RoomType.RestSite => "Rest",
            RoomType.Event => "Event",
            RoomType.Treasure => "Treasure",
            _ => "Unknown"
        };
    }

    // Maps the room a deck change was detected in to a human-readable acquisition source.
    private static string RoomSource(AbstractRoom? room)
    {
        return room?.RoomType switch
        {
            RoomType.Monster or RoomType.Elite or RoomType.Boss => "Reward",
            RoomType.Shop => "Shop",
            RoomType.RestSite => "Rest",
            RoomType.Event => "Event",
            RoomType.Treasure => "Treasure",
            _ => "Unknown"
        };
    }
}
