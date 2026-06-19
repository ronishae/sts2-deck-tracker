using System.Text.Json;
using Godot;

namespace DeckTracker;

public static partial class CardRegistry
{
    // One save file per run, keyed by run seed, so different runs (other profiles / multiplayer lobbies)
    // can each be resumed instead of only the most recent. Growth is bounded by MaxStoredRuns (LRU).
    private static readonly string SaveDirectory = ProjectSettings.GlobalizePath("user://deck_tracker_saves/");
    private const int MaxStoredRuns = 5;

    // Set by Publish() on any ledger mutation; read+cleared once per frame by DrainPendingSnapshot so the
    // expensive ledger clone happens at most once per rendered frame instead of once per damage event.
    private static volatile bool _publishPending;

    // Called when the game sets up a brand-new run (not a load/resume). Forces the next SyncRun to start
    // clean even if the seed matches a prior run's save, so a same-seed restart never shows stale data.
    public static void BeginNewRun()
    {
        lock (SyncRoot)
        {
            _pendingFreshRun = true;
            _currentRunSeed = "";   // ensure SyncRun re-initialises even on the same seed
            ResetRun();             // wipe immediately so the overlay doesn't briefly show the old run
            Log.Info("BeginNewRun. New run starting; tracker reset.");
        }
    }

    public static void SyncRun(string runSeed)
    {
        if (string.IsNullOrEmpty(runSeed))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_currentRunSeed == runSeed && !_pendingFreshRun)
            {
                return;
            }

            _currentRunSeed = runSeed;

            var resumed = !_pendingFreshRun && TryLoadState(runSeed);
            if (resumed)
            {
                Log.Info($"SyncRun. Resumed run data for seed: {runSeed}");
                // Resuming replays the active combat from its start, but the mod's combat accumulators
                // (ForgeHistory, Sovereign Blade history, buff/poison/orb ledgers, generation seam maps)
                // are normally cleared only at combat end — which never fired before the save/quit. Clear
                // them now so the replayed combat starts clean; the loaded run stats (EntityLedger) are kept
                // and deck identity is rebuilt by RestoreLiveInstances below.
                ResetInternalsCombat();
            }
            else
            {
                Log.Info($"SyncRun. Starting fresh tracker for seed: {runSeed}");
                ResetRun();
            }
            _pendingFreshRun = false;
            RestoreLiveInstances();

            // A fresh run starts a new export log now that the players (character/ascension) are restored.
            // A resumed run already adopted its log from the save file inside TryLoadState; it only needs its
            // gold baseline re-synced to the live total so the next gold gain reports a correct delta.
            var liveRun = GetLiveRunState();
            if (resumed)
            {
                RunLogRecorder.SetGoldBaseline(FirstPlayerGold(liveRun));
            }
            else
            {
                RunLogRecorder.BeginRun(runSeed, ExtractCharacterLabel(liveRun), liveRun?.AscensionLevel ?? 0, FirstPlayerGold(liveRun), GameRelease.Version);
            }
        }
        Publish();
    }

    public static void SaveState()
    {
        try
        {
            SavedRunState state;
            lock (SyncRoot)
            {
                if (string.IsNullOrEmpty(_currentRunSeed))
                {
                    return;
                }

                state = new SavedRunState
                {
                    RunSeed = _currentRunSeed,
                    PotionCounter = _potionCounter,
                    Totals = EntityLedger.Values.OfType<CardStats>()
                        .ToDictionary(s => s.Id, s => (CardStats)s.Clone()),
                    Potions = EntityLedger.Values.OfType<PotionStats>()
                        .ToDictionary(s => s.Id, s => (PotionStats)s.Clone()),
                    Relics = EntityLedger
                        .Where(kvp => kvp.Value is RelicStats)
                        .ToDictionary(kvp => kvp.Key, kvp => (RelicStats)kvp.Value.Clone())
                };
            }

            // Persist the run's export log alongside the stats so a resumed run keeps its timeline and the
            // master-CSV high-water mark. Read outside the SyncRoot lock above (RunLogRecorder has its own).
            state.RunLog = RunLogRecorder.CurrentLog;

            System.IO.Directory.CreateDirectory(SaveDirectory);
            var path = GetRunSavePath(state.RunSeed);
            string json = JsonSerializer.Serialize(state, SavedStateCtx.Default.SavedRunState);
            System.IO.File.WriteAllText(path, json);
            Log.Info($"SaveState. State saved successfully. Seed: {state.RunSeed}, Path: {path}");

            EvictOldRuns();
        }
        catch (Exception e)
        {
            Log.Error($"SaveState Failed: {e.Message}");
        }
    }

    // Maps a run seed to its save file path. Sanitises the seed to filesystem-safe characters and appends
    // a short stable hash so two distinct seeds can never collide to the same name. The authoritative seed
    // check lives inside the file (TryLoadState validates state.RunSeed), so the name only needs to be unique.
    private static string GetRunSavePath(string seed)
    {
        var path = System.IO.Path.Combine(SaveDirectory, $"{GetRunFileStem(seed)}.json");
        Log.VeryDebug($"GetRunSavePath. Seed: {seed}, Path: {path}");
        return path;
    }

    // The per-run filename stem (sanitised seed + stable hash, no extension), shared by the internal save
    // file and the user-facing export JSON so both resolve to the same name for a given run.
    public static string GetRunFileStem(string seed)
    {
        var sanitized = new string(seed.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return $"{sanitized}_{StableHash(seed)}";
    }

    // Deterministic 8-char hex hash of the seed (string.GetHashCode is not stable across processes).
    private static string StableHash(string value)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var bytes = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes, 0, 4);
    }

    // LRU cap: keep only the MaxStoredRuns most-recently-written run files, deleting the oldest beyond that.
    // Each SaveState refreshes the active run's timestamp, so the run being played is never evicted.
    private static void EvictOldRuns()
    {
        try
        {
            var files = System.IO.Directory.GetFiles(SaveDirectory, "*.json");
            if (files.Length <= MaxStoredRuns)
            {
                Log.VeryDebug($"EvictOldRuns. Within cap. Count: {files.Length}, Max: {MaxStoredRuns}");
                return;
            }

            var oldest = files
                .OrderBy(System.IO.File.GetLastWriteTimeUtc)
                .Take(files.Length - MaxStoredRuns)
                .ToList();
            foreach (var file in oldest)
            {
                System.IO.File.Delete(file);
            }
            Log.Info($"EvictOldRuns. Removed old run saves. Removed: {oldest.Count}, Remaining: {MaxStoredRuns}");
        }
        catch (Exception e)
        {
            Log.Warn($"EvictOldRuns Failed: {e.Message}");
        }
    }

    private static bool TryLoadState(string targetSeed)
    {
        try
        {
            var path = GetRunSavePath(targetSeed);
            if (!System.IO.File.Exists(path))
            {
                Log.Debug($"TryLoadState. No save file found. Seed: {targetSeed}, Path: {path}");
                return false;
            }

            string json = System.IO.File.ReadAllText(path);
            SavedRunState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedRunState);

            if (state == null || state.RunSeed != targetSeed)
            {
                Log.Debug($"TryLoadState. Seed mismatch or null state. Expected: {targetSeed}, Got: {state?.RunSeed}");
                return false;
            }

            EntityLedger.Clear();
            foreach (var kvp in state.Totals) EntityLedger[kvp.Key] = kvp.Value;
            foreach (var kvp in state.Potions ?? new Dictionary<string, PotionStats>()) EntityLedger[kvp.Key] = kvp.Value;
            foreach (var kvp in state.Relics ?? new Dictionary<string, RelicStats>()) EntityLedger[kvp.Key] = kvp.Value;
            _potionCounter = state.PotionCounter;
            RunLogRecorder.RestoreFromSave(state.RunLog);
            return true;
        }
        catch (Exception e)
        {
            Log.Error($"TryLoadState Failed: {e.Message}");
            return false;
        }
    }

    public static void ForcePublish()
    {
        Publish();
    }

    // Signals that the ledger changed. Cheap by design: the actual snapshot clone is deferred to
    // DrainPendingSnapshot (pulled once per frame by the overlay), so per-event mutations never clone.
    private static void Publish() => _publishPending = true;

    // Returns a fresh immutable snapshot for the overlay when state changed since the last pull, else null.
    // Cloning under SyncRoot gives the render thread a stable copy; called once per Godot process frame.
    public static List<CardStats>? DrainPendingSnapshot()
    {
        if (!_publishPending)
        {
            return null;
        }
        lock (SyncRoot)
        {
            if (!_publishPending)
            {
                return null;
            }
            _publishPending = false;
            return EntityLedger.Values.OfType<CardStats>().Select(s => (CardStats)s.Clone()).ToList();
        }
    }
}
