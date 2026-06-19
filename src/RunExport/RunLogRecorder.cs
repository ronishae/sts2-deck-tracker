namespace DeckTracker;

// Accumulates the active run's RunLog from game events and drives the automatic CSV append.
// Holds all its own state behind a private lock and is a pure data accumulator — game types are
// translated to primitives by the hook layer before reaching here. No-ops when no run is active.
public static class RunLogRecorder
{
    private static readonly object Lock = new();

    private static RunLog? _log;
    private static CombatRecord? _currentCombat;

    public static void BeginRun(string seed, string character, int ascensionLevel, string gameVersion)
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
            Log.Info($"BeginRun. Seed: {seed}, Character: {character}, Ascension: {ascensionLevel}, GameVersion: {gameVersion}");
        }
    }

    // Adopts a RunLog loaded from a save file so a resumed run keeps its combat history and CSV high-water mark.
    public static void RestoreFromSave(RunLog? saved)
    {
        lock (Lock)
        {
            _log = saved;
            _currentCombat = null;
            Log.Info($"RestoreFromSave. Restored: {saved != null}, Combats: {saved?.Combats.Count ?? 0}");
        }
    }

    public static void Reset()
    {
        lock (Lock)
        {
            _log = null;
            _currentCombat = null;
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

    public static void StartCombat(int floor, int act, string actName, string combatType, string encounterId)
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
                EncounterId = encounterId
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

    public static void EndCombat(bool playerAlive, List<EntityFightStat> contributions)
    {
        lock (Lock)
        {
            if (_log == null || _currentCombat == null)
            {
                Log.Warn("EndCombat. No active combat record; skipping.");
                return;
            }

            var combat = _currentCombat;
            combat.Outcome = playerAlive ? "Won" : "Died";
            combat.Contributions = contributions;
            _log.Combats.Add(combat);

            Log.Info($"EndCombat. Index: {combat.Index}, Outcome: {combat.Outcome}, Turns: {combat.Turns}, DmgTaken: {combat.DamageTaken}");
            _currentCombat = null;

            RunExporter.AppendFightRows(_log);
        }
    }
}
