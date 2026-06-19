using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static void ResetInternalsCombat()
    {
        lock (SyncRoot)
        {
            _currentCombatType = "Unknown";
            _incrementedThisCombat.Clear();
            _cardCreatedBy.Clear();
            _cardOrigin.Clear();
            _cardGeneratedBy.Clear();
            _cardGeneratedByImmediate.Clear();
            _cardCurrentTrackingId.Clear();
            ResetForgeState();
            ResetSovereignBladeState();
            ResetNecroMasteryState();
            ResetFanOfKnivesState();
            ResetPoisonState();
            ResetReaperFormState();
            ResetDoomState();
            ResetCountdownState();
            ResetReflectState();
            ResetOrbState();
            ResetBuffState();
            ResetRitualState();

            List<ITrackerState> trackers = new();
            trackers.AddRange(SimpleDamageTrackers.Values);
            trackers.AddRange(TargetedTrackers.Values);
            trackers.AddRange(HandoffTrackers.Values);
            trackers.AddRange(ProportionalTrackers.Values);
            trackers.AddRange(QueueTrackers.Values);
            trackers.Add(InstancedTracker);

            foreach (var tracker in trackers)
            {
                tracker.Reset();
            }
            Log.Info("ResetInternalsCombat. All state reset.");
        }
    }

    public static void ClearSession()
    {
        lock (SyncRoot)
        {
            _currentRunSeed = "";
            Log.Info("ClearSession. Session cleared.");
        }
    }

    public static void ResetRun()
    {
        lock (SyncRoot)
        {
            EntityLedger.Clear();
            _currentAct = 1;
            _isRunEnding = false;
            ResetInternalsCombat();
            RelicExecutionManager.ResetState();
            RelicNameCache.Clear();
            _relicOwnerNetIdByModel.Clear();
            PotionInstanceIds.Clear();
            _potionCounter = 0;
            _cardInstanceIds.Clear();
            _cardInstanceCounters.Clear();
            _deckScanOrdinals.Clear();
            _removedDeckCardIds.Clear();
            _playerIndexByNetId.Clear();
            PlayerLabels.Clear();
            _steamNameCache.Clear();
            RunLogRecorder.Reset();
            Log.Info("ResetRun. Run state cleared.");
        }
        Publish();
    }

    // True while a combat is in progress (set at StartCombat, cleared to "Unknown" by ResetInternalsCombat at
    // combat end). Used to gate the out-of-combat deck-change poll away from mid-combat rescans.
    public static bool IsCombatActive => _currentCombatType != "Unknown";

    public static void SyncDeckState(int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            var copyCounts = activeDeckIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var uniqueActiveIds = new HashSet<string>(activeDeckIds);

            Log.Debug($"SyncDeckState. Floor: {currentFloor}, Active Count: {activeDeckIds.Count}");
            foreach (var stat in EntityLedger.Values.OfType<CardStats>())
            {
                if (stat.IsActive && !uniqueActiveIds.Contains(stat.Id))
                {
                    Log.VeryDebug($"  -> {stat.Id} removed from deck");
                    stat.IsActive = false;
                    stat.CopiesInDeck = 0;

                    var floorLeft = Math.Max(1, currentFloor - 1);
                    if (stat.FloorRemoved == -1)
                    {
                        stat.FloorLeftDeck = floorLeft;
                    }
                }
                else if (copyCounts.TryGetValue(stat.Id, out var count))
                {
                    stat.IsActive = true;
                    stat.CopiesInDeck = count;
                    // A card present in the live deck is not removed — clear any stale removal markers so a
                    // card that was temporarily taken (e.g. stolen by a Thieving Hopper) and returned shows
                    // N/A again. A genuinely lost card never re-enters this branch, so its FloorRemoved stays.
                    stat.FloorRemoved = -1;
                    stat.FloorLeftDeck = -1;
                }
            }
        }
        Publish();
    }

    public static void StartCombat(string combatType, int currentFloor, int currentAct, List<string> activeDeckIds)
    {
        SyncDeckState(currentFloor, activeDeckIds);

        lock (SyncRoot)
        {
            _currentAct = currentAct;
            _currentCombatType = combatType;
            Log.Info($"StartCombat. Type: {_currentCombatType}, Act: {_currentAct}");

            foreach (var entity in EntityLedger.Values)
            {
                entity.CombatDamage = 0;
                entity.GeneratedCombatDamage = 0;
                entity.RawForgeCombat = 0;
                entity.ConnectedForgeCombat = 0;
                entity.ReceivedForgeCombat = 0;
                entity.CombatTimesDrawn = 0;
                entity.CombatTimesPlayed = 0;

                if (!entity.IsActive || entity is PotionStats)
                {
                    continue;
                }

                entity.GetAct(_currentAct)?.AddEncounterSeen(combatType);

                if (entity is CardStats)
                {
                    _incrementedThisCombat.Add(entity.Id);
                }
            }
        }
        Publish();
    }

    public static void ProcessCombatEnd()
    {
        Log.Info("ProcessCombatEnd.");
        // Capture each card's per-combat contribution into the run export log before ResetInternalsCombat,
        // while the per-combat fields are still intact. SaveState below then persists the updated log.
        FinalizeCombatExport();
        lock (SyncRoot)
        {
            ResetInternalsCombat();
        }
        SaveState();
        Publish();
    }

    // Captures the combat that just killed the player. A run-ending loss never fires AfterCombatEnd, so the
    // fatal fight would otherwise be missing from the export — this records it (with the player dead, so its
    // outcome is "Died"), resets combat state, and persists. Safe if no combat is open (EndCombat no-ops).
    public static void FinalizeFatalCombat()
    {
        // Set before ResetInternalsCombat so that any PlayerRemoveRelicPostfix calls fired by
        // game cleanup after death are suppressed in HandleRelicRemove.
        _isRunEnding = true;
        FinalizeCombatExport();
        lock (SyncRoot)
        {
            ResetInternalsCombat();
        }
        SaveState();
    }

    // Snapshots the just-ended combat (each entity's contribution + the local player's alive state) into
    // the run log, which triggers the CSV write inside RunLogRecorder.EndCombat.
    private static void FinalizeCombatExport()
    {
        var contributions = BuildCombatEntityStats();
        var run = GetLiveRunState();
        var player = run != null && run.Players.Count > 0 ? run.Players[0] : null;
        var alive = player?.Creature.IsAlive ?? true;
        RunLogRecorder.EndCombat(alive, contributions);
    }

    // Builds the per-entity stats for the combat that just ended from the live per-combat fields. Cards are
    // included when they participated (drawn / dealt or generated damage); relics and potions only when they
    // dealt damage this fight, so non-damaging relics don't flood every combat row.
    private static List<EntityFightStat> BuildCombatEntityStats()
    {
        lock (SyncRoot)
        {
            var participants = EntityLedger.Values.Where(ParticipatedThisCombat).ToList();
            var totalDamage = participants.Sum(e => e.CombatDamage);
            return participants.Select(e => BuildEntityFightStat(e, totalDamage)).ToList();
        }
    }

    private static bool ParticipatedThisCombat(EntityStats entity)
    {
        if (entity is CardStats card)
        {
            if (card.CardType == "Status")
            {
                return false;
            }
            // Always include active deck cards so the export represents a full deck snapshot per fight.
            // Generated/combat cards (IsActive=false, e.g. Shivs) fall through to the draw/damage check.
            return entity.IsActive || entity.CombatTimesDrawn > 0 || entity.CombatDamage > 0 || entity.GeneratedCombatDamage > 0;
        }
        if (entity is PotionStats)
        {
            // Include potions that dealt damage OR were used/discarded this combat (CombatTimesPlayed is
            // incremented by MarkPotionUsed/MarkPotionDiscarded when combat is active).
            return entity.CombatDamage > 0 || entity.CombatTimesPlayed > 0;
        }
        return entity.CombatDamage > 0;
    }

    private static EntityFightStat BuildEntityFightStat(EntityStats entity, decimal totalDamage)
    {
        var stat = new EntityFightStat
        {
            Name = entity.DisplayName,
            PlayerIndex = entity.PlayerIndex,
            Damage = entity.CombatDamage,
            GeneratedDamage = entity.GeneratedCombatDamage,
            DamageContribPct = totalDamage > 0 ? Math.Round(entity.CombatDamage / totalDamage * 100, 2) : 0,
            RawForge = entity.RawForgeCombat,
            ConnectedForge = entity.ConnectedForgeCombat,
            ReceivedForge = entity.ReceivedForgeCombat
        };

        switch (entity)
        {
            case CardStats card:
                var (copyIndex, ownerNetId) = ParseCardId(card.Id);
                stat.EntityType = "Card";
                stat.FloorAdded = card.FloorAdded;
                stat.CopyIndex = copyIndex;
                stat.OwnerNetId = ownerNetId;
                stat.UpgradeLevel = card.UpgradeLevel;
                stat.Enchantment = card.Enchantment;
                stat.Rarity = (card.Model as CardModel)?.Rarity.ToString() ?? "";
                stat.TimesDrawn = card.CombatTimesDrawn;
                stat.TimesPlayed = card.CombatTimesPlayed;
                stat.PlayRate = card.CombatTimesDrawn > 0
                    ? Math.Round((decimal)card.CombatTimesPlayed / card.CombatTimesDrawn, 4)
                    : 0;
                if (!string.IsNullOrEmpty(card.GeneratedById)
                    && EntityLedger.TryGetValue(card.GeneratedById, out var generator))
                {
                    stat.GeneratedBy = generator.DisplayName;
                }
                break;
            case RelicStats relic:
                stat.EntityType = "Relic";
                stat.FloorAdded = relic.FloorAdded;
                stat.Rarity = relic.Rarity;
                break;
            case PotionStats potion:
                stat.EntityType = "Potion";
                stat.Rarity = (potion.Model as PotionModel)?.Rarity.ToString() ?? "";
                // -1 is the "not yet" sentinel for these floors; map it to null so the CSV cell is blank.
                stat.FloorObtained = potion.FloorObtained >= 0 ? potion.FloorObtained : null;
                stat.FloorUsed = potion.FloorUsed >= 0 ? potion.FloorUsed : null;
                stat.FloorDiscarded = potion.FloorDiscarded >= 0 ? potion.FloorDiscarded : null;
                stat.OwnerNetId = potion.OwnerNetId ?? "";
                break;
        }
        return stat;
    }

    // Extracts the copy index and owner NetId from a card tracking id ("..._C{copy}_U{up}_{enchant}_P{owner}").
    // Returns blanks for non-card ids and for the "NONE" owner sentinel.
    private static (int? copyIndex, string ownerNetId) ParseCardId(string id)
    {
        var match = System.Text.RegularExpressions.Regex.Match(id, @"_C(\d+)_U\d+_.*_P([^_]+)$");
        if (!match.Success)
        {
            return (null, "");
        }
        var copyIndex = int.TryParse(match.Groups[1].Value, out var parsed) ? parsed : (int?)null;
        var ownerNetId = match.Groups[2].Value;
        return (copyIndex, ownerNetId == UnknownOwnerKey ? "" : ownerNetId);
    }

    private static int FirstPlayerGold(IRunState? run) => run != null && run.Players.Count > 0 ? run.Players[0].Gold : 0;

    // The display label for the run's character(s), joined for co-op. Used to stamp the export log at start.
    private static string ExtractCharacterLabel(IRunState? run)
    {
        if (run == null || run.Players.Count == 0)
        {
            return "";
        }
        try
        {
            return string.Join(" + ", run.Players.Select(p => p.Character.Title.GetFormattedText()));
        }
        catch (Exception e)
        {
            Log.Warn($"ExtractCharacterLabel failed: {e.Message}");
            return "";
        }
    }

}
