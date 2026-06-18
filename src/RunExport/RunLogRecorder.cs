namespace DeckTracker;

// Accumulates the active run's RunLog from game events and drives the automatic exports (JSON at every
// combat end, plus the master CSV append). Holds all its own state behind a private lock and is a pure
// data accumulator — game types are translated to primitives by the hook layer (HookPatches.RunLog.cs)
// before reaching here, so this class never touches the game API. No-ops when no run is active.
public static class RunLogRecorder
{
    private static readonly object Lock = new();

    private static RunLog? _log;
    private static CombatRecord? _currentCombat;

    // Last observed gold total, used to turn AfterGoldGained's running total into a per-gain delta.
    // Re-baselined on run start/resume and after purchases (which the gold hook does not fire for).
    private static int _lastGold;

    // Deck composition at the last out-of-combat sync, used to diff adds/removes/upgrades. Not persisted:
    // after a resume we re-baseline from the live deck (see _deckBaselinePending) instead of replaying.
    private static Dictionary<string, DeckCardInfo> _previousDeck = new();
    private static bool _deckBaselinePending;

    // Run outcome value for a player-abandoned run. Constant so the JSON stays stable and consumers can
    // distinguish an abandon from a genuine "Death".
    public const string OutcomeAbandoned = "Abandoned";

    // Timeline EventType values. Kept as constants so call sites can't typo them and the JSON stays stable.
    public const string EventRoomEntered = "RoomEntered";
    public const string EventActEntered = "ActEntered";
    public const string EventCombat = "Combat";
    public const string EventReward = "Reward";
    public const string EventRewardSkipped = "RewardSkipped";
    public const string EventPurchase = "Purchase";
    public const string EventRest = "Rest";
    public const string EventRelicGained = "RelicGained";
    public const string EventPotionGained = "PotionGained";
    public const string EventGoldGained = "GoldGained";

    public static void BeginRun(string seed, string character, int ascensionLevel, int startingGold, string gameVersion)
    {
        lock (Lock)
        {
            _log = new RunLog
            {
                RunSeed = seed,
                StartedAtUtc = DateTime.UtcNow.ToString("o"),
                Character = character,
                AscensionLevel = ascensionLevel,
                GameVersion = gameVersion
            };
            _currentCombat = null;
            _lastGold = startingGold;
            _previousDeck = new Dictionary<string, DeckCardInfo>();
            _deckBaselinePending = false;
            Log.Info($"BeginRun. Seed: {seed}, Character: {character}, Ascension: {ascensionLevel}, Gold: {startingGold}, GameVersion: {gameVersion}");
        }
    }

    // Re-syncs the gold baseline to a known total without emitting an event. Called on resume and after a
    // purchase so the next AfterGoldGained delta is measured from the correct starting point.
    public static void SetGoldBaseline(int gold)
    {
        lock (Lock)
        {
            _lastGold = gold;
        }
    }

    // Adopts a RunLog loaded from a save file so a resumed run keeps its timeline. The live deck is
    // re-baselined on the next sync rather than diffed, so resuming never logs the whole deck as "Added".
    public static void RestoreFromSave(RunLog? saved)
    {
        lock (Lock)
        {
            _log = saved;
            _currentCombat = null;
            _previousDeck = new Dictionary<string, DeckCardInfo>();
            _deckBaselinePending = saved != null;
            Log.Info($"RestoreFromSave. Restored: {saved != null}, Combats: {saved?.Combats.Count ?? 0}");
        }
    }

    public static void Reset()
    {
        lock (Lock)
        {
            _log = null;
            _currentCombat = null;
            _lastGold = 0;
            _previousDeck = new Dictionary<string, DeckCardInfo>();
            _deckBaselinePending = false;
            Log.Debug("Reset. Run log cleared.");
        }
    }

    // The live RunLog, handed to SaveState so it is persisted inside the run's save file. Null when idle.
    public static RunLog? CurrentLog
    {
        get
        {
            lock (Lock)
            {
                return _log;
            }
        }
    }

    public static void FinalizeRun(int finalFloor, int finalGold)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            _log.FinalFloor = finalFloor;
            _log.FinalGold = finalGold;
            Log.Info($"FinalizeRun. Floor: {finalFloor}, Gold: {finalGold}, Outcome: {_log.Outcome}");
            RunExporter.ExportRun(_log);
        }
    }

    public static void MarkVictory(int floor)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            _log.Outcome = "Victory";
            Log.Info($"MarkVictory. Floor: {floor}");
        }
    }

    // Marks the run as a loss. Called from the player-death hook because a run-ending death does not fire
    // AfterCombatEnd, so EndCombat's own death branch never runs. killedBy falls back to the open combat's
    // encounter when the hook can't supply one (e.g. a death outside combat).
    public static void MarkDeath(int floor, int finalGold, string killedBy)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            _log.Outcome = "Death";
            _log.FloorDied = floor;
            _log.FinalFloor = floor;
            _log.FinalGold = finalGold;
            _log.KilledBy = !string.IsNullOrEmpty(killedBy) ? killedBy : _currentCombat?.EncounterId ?? "";
            Log.Info($"MarkDeath. Floor: {floor}, Gold: {finalGold}, KilledBy: {_log.KilledBy}");
        }
    }

    // Marks the active run as player-abandoned. Called from the in-game abandon path (RunManager.CleanUp
    // with IsAbandoned set). The override is unconditional because the game force-kills the players when
    // abandoning, which already recorded a spurious "Death" via the AfterDeath hook that must be replaced.
    public static void MarkAbandoned()
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            ApplyAbandoned(_log);
            Log.Info($"MarkAbandoned. Floor: {_log.FloorDied}, Outcome: {_log.Outcome}");
        }
    }

    // Marks a detached RunLog (one loaded from a previous export, not the active session) as abandoned.
    // Used by the main-menu abandon path, where no run is loaded.
    public static void MarkAbandoned(RunLog log)
    {
        ApplyAbandoned(log);
        Log.Info($"MarkAbandoned (Detached). Seed: {log.RunSeed}, Floor: {log.FloorDied}, Outcome: {log.Outcome}");
    }

    // Keeps the floor the run was abandoned on: the in-game path already has FloorDied set by the
    // force-kill's MarkDeath, while a save-&-quit log carries the floor reached in FinalFloor.
    private static void ApplyAbandoned(RunLog log)
    {
        log.Outcome = OutcomeAbandoned;
        log.KilledBy = "Run Abandoned";
        if (log.FloorDied < 0)
        {
            log.FloorDied = log.FinalFloor;
        }

        // The game force-kills the player on abandon, which records the last combat as "Died".
        // Override it to "Abandoned" so the CSV reflects the true reason for the combat ending.
        if (log.Combats.Count > 0 && log.Combats[^1].Outcome == "Died")
        {
            log.Combats[^1].Outcome = OutcomeAbandoned;
        }
    }

    public static void RecordMap(int actIndex, List<MapNodeSnapshot> nodes)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            _log.Maps.RemoveAll(m => m.ActIndex == actIndex);
            _log.Maps.Add(new ActMapSnapshot { ActIndex = actIndex, Nodes = nodes });
            Log.Info($"RecordMap. Act: {actIndex}, Nodes: {nodes.Count}");
        }
    }

    public static void RecordRoomEntered(int act, int floor, int col, int row, string pointType)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            var last = _log.Path.Count > 0 ? _log.Path[^1] : null;
            if (last == null || last.Col != col || last.Row != row || last.Floor != floor)
            {
                _log.Path.Add(new PathStep { Act = act, Floor = floor, Col = col, Row = row, PointType = pointType });
            }
            AddEvent(EventRoomEntered, floor, act, pointType, null, null, null);
            Log.Info($"RecordRoomEntered. Floor: {floor}, Type: {pointType}");
        }
    }

    public static void RecordActEntered(int act, int floor, string actName)
    {
        lock (Lock)
        {
            AddEvent(EventActEntered, floor, act, actName, $"Act {act}", null, null);
        }
    }

    public static void StartCombat(int floor, int act, string actName, string combatType, string encounterId, int hpBefore, int goldBefore)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }
            _currentCombat = new CombatRecord
            {
                Index = _log.Combats.Count,
                Floor = floor,
                Act = act,
                ActName = actName,
                CombatType = combatType,
                EncounterId = encounterId,
                PlayerHpBefore = hpBefore,
                GoldBefore = goldBefore
            };
            Log.Info($"StartCombat. Index: {_currentCombat.Index}, Floor: {floor}, Encounter: {encounterId}");
        }
    }

    public static void IncrementTurn()
    {
        lock (Lock)
        {
            if (_currentCombat != null)
            {
                _currentCombat.Turns++;
            }
        }
    }

    public static void AddDamageTaken(decimal amount)
    {
        lock (Lock)
        {
            if (_currentCombat != null && amount > 0)
            {
                _currentCombat.DamageTaken += amount;
            }
        }
    }

    public static void AddBlockGained(decimal amount)
    {
        lock (Lock)
        {
            if (_currentCombat != null && amount > 0)
            {
                _currentCombat.BlockGained += amount;
            }
        }
    }

    public static void EndCombat(int hpAfter, bool playerAlive, List<EntityFightStat> contributions)
    {
        lock (Lock)
        {
            if (_log == null || _currentCombat == null)
            {
                Log.Warn("EndCombat. No active combat record; skipping.");
                return;
            }

            var combat = _currentCombat;
            combat.PlayerHpAfter = hpAfter;
            combat.Outcome = playerAlive ? "Won" : "Died";
            combat.Contributions = contributions;
            _log.Combats.Add(combat);

            if (!playerAlive)
            {
                _log.Outcome = "Death";
                _log.FloorDied = combat.Floor;
                _log.KilledBy = combat.EncounterId;
            }

            AddEvent(EventCombat, combat.Floor, combat.Act, combat.EncounterId,
                combat.Outcome, combat.DamageTaken, combat.Index);

            Log.Info($"EndCombat. Index: {combat.Index}, Outcome: {combat.Outcome}, Turns: {combat.Turns}, DmgTaken: {combat.DamageTaken}");
            _currentCombat = null;

            RunExporter.ExportRun(_log);
            RunExporter.AppendFightRows(_log);
        }
    }

    public static void RecordReward(int floor, int act, string rewardType, string detail, bool taken)
    {
        lock (Lock)
        {
            AddEvent(taken ? EventReward : EventRewardSkipped, floor, act, rewardType, detail, null, null);
            Log.Debug($"RecordReward. Type: {rewardType}, Taken: {taken}, Detail: {detail}");
        }
    }

    public static void RecordPurchase(int floor, int act, string itemLabel, int goldSpent)
    {
        lock (Lock)
        {
            AddEvent(EventPurchase, floor, act, itemLabel, null, goldSpent, null);
            Log.Debug($"RecordPurchase. Item: {itemLabel}, Gold: {goldSpent}");
        }
    }

    public static void RecordRest(int floor, int act, bool isSmith)
    {
        lock (Lock)
        {
            AddEvent(EventRest, floor, act, isSmith ? "Smith" : "Heal", null, null, null);
            Log.Debug($"RecordRest. Smith: {isSmith}");
        }
    }

    public static void RecordRelicGained(int floor, int act, string relicName)
    {
        lock (Lock)
        {
            AddEvent(EventRelicGained, floor, act, relicName, null, null, null);
            Log.Debug($"RecordRelicGained. Relic: {relicName}");
        }
    }

    public static void RecordPotionGained(int floor, int act, string potionName)
    {
        lock (Lock)
        {
            AddEvent(EventPotionGained, floor, act, potionName, null, null, null);
            Log.Debug($"RecordPotionGained. Potion: {potionName}");
        }
    }

    // Records the amount of gold gained (the delta vs the last observed total), labelled with the room it
    // came from. A non-positive delta (e.g. gold spent, or a stale baseline) is ignored but still re-syncs
    // the baseline so the next gain is measured correctly.
    public static void RecordGoldGained(int floor, int act, int currentTotal, string roomLabel)
    {
        lock (Lock)
        {
            var delta = currentTotal - _lastGold;
            _lastGold = currentTotal;
            if (delta <= 0)
            {
                return;
            }
            AddEvent(EventGoldGained, floor, act, roomLabel, null, delta, null);
            Log.Debug($"RecordGoldGained. Delta: {delta}, Total: {currentTotal}, Room: {roomLabel}");
        }
    }

    // Diffs the current out-of-combat deck against the previous sync and logs adds/removes. An add and a
    // remove that share a BaseCardKey in the same diff are reported as an upgrade/enchant rather than a
    // separate remove+add, since a version change rotates the card's tracking id.
    public static void SyncDeck(int floor, string source, List<DeckCardInfo> deck)
    {
        lock (Lock)
        {
            if (_log == null)
            {
                return;
            }

            var current = new Dictionary<string, DeckCardInfo>();
            foreach (var card in deck)
            {
                current[card.Id] = card;
            }

            if (_deckBaselinePending)
            {
                _previousDeck = current;
                _deckBaselinePending = false;
                Log.Debug($"SyncDeck. Baseline set. Cards: {current.Count}");
                return;
            }

            DiffDeck(floor, source, current);
            _previousDeck = current;
        }
    }

    private static void DiffDeck(int floor, string source, Dictionary<string, DeckCardInfo> current)
    {
        var addedIds = current.Keys.Where(id => !_previousDeck.ContainsKey(id)).ToList();
        var removedIds = _previousDeck.Keys.Where(id => !current.ContainsKey(id)).ToList();
        if (addedIds.Count == 0 && removedIds.Count == 0)
        {
            return;
        }

        var unconsumedRemovals = removedIds.ToHashSet();
        foreach (var addedId in addedIds)
        {
            var added = current[addedId];
            var match = removedIds.FirstOrDefault(rid => unconsumedRemovals.Contains(rid)
                && _previousDeck[rid].BaseKey == added.BaseKey);
            if (match != null)
            {
                unconsumedRemovals.Remove(match);
                AddDeckChange(floor, "Upgraded", added.Id, added.DisplayName, source);
                continue;
            }
            AddDeckChange(floor, "Added", added.Id, added.DisplayName, source);
        }

        foreach (var removedId in removedIds.Where(unconsumedRemovals.Contains))
        {
            var removed = _previousDeck[removedId];
            AddDeckChange(floor, "Removed", removed.Id, removed.DisplayName, source);
        }
    }

    private static void AddDeckChange(int floor, string changeType, string cardId, string name, string source)
    {
        if (_log == null)
        {
            return;
        }
        _log.DeckChanges.Add(new DeckChangeEvent
        {
            Floor = floor,
            ChangeType = changeType,
            CardId = cardId,
            DisplayName = name,
            Source = source
        });
        Log.Debug($"AddDeckChange. {changeType}: {name} ({source})");
    }

    // Caller must hold Lock.
    private static void AddEvent(string type, int floor, int act, string? label, string? detail, decimal? amount, int? combatIndex)
    {
        if (_log == null)
        {
            return;
        }
        _log.Timeline.Add(new TimelineEvent
        {
            EventType = type,
            Floor = floor,
            Act = act,
            Label = label,
            Detail = detail,
            Amount = amount,
            CombatIndex = combatIndex
        });
    }
}
