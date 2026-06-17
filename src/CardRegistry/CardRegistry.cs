using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Runs;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly object SyncRoot = new();
    
    public static Dictionary<string, EntityStats> EntityLedger = new();
    private static string _currentRunSeed = "";
    // Set by BeginNewRun when the game starts a fresh run; makes the next SyncRun reset rather than resume,
    // even if the seed matches a previous run's save (same-seed restart).
    private static bool _pendingFreshRun;
    
    private static int _currentAct = 1;
    private static string _currentCombatType = "Unknown";
    
    private static HashSet<string> _incrementedThisCombat = new();

    // Maps player index (order in IRunState.Players) to the character's display name
    public static readonly Dictionary<int, string> PlayerLabels = new();

    // Maps a player's stable NetId (string) to their current ordered index. Rebuilt every deck scan.
    // Used only to colour/order rows (CardStats.PlayerIndex) — never baked into a tracking ID.
    private static readonly Dictionary<string, int> _playerIndexByNetId = new();

    // Sentinel owner key for cards with no resolvable owning player (e.g. enemy-owned cards).
    private const string UnknownOwnerKey = "NONE";

    // Per-physical-copy index, so identical copies are distinct rows in the overlay (which then stacks them).
    // _cardInstanceIds maps a live CardModel -> its copy index. For deck cards it is rebuilt deterministically
    // every scan (count-capped ordinal) so it stays stable across multiplayer client re-syncs; generated/combat
    // cards fall back to a persistent per-{baseId}_F{floor} counter.
    private static readonly Dictionary<CardModel, int> _cardInstanceIds = new();
    private static readonly Dictionary<string, int> _cardInstanceCounters = new();
    // Transient per-scan ordinal counters, keyed by "{netId}|{baseId}_F{floor}_U{upgrade}_{enchant}".
    private static readonly Dictionary<string, int> _deckScanOrdinals = new();
    // Shared "created by" map: the model that created a transient combat card. Auto-filled from the
    // game's CloneOf for clones (Anger); generated cards like Shiv will populate this manually from the
    // playing card/potion/power context (planned follow-up). Cleared each combat (transient cards only).
    private static readonly Dictionary<CardModel, AbstractModel> _cardCreatedBy = new();
    // Memoized origin: maps a card to the persistent deck card its damage should roll up to, so we never
    // re-walk the chain. A clone inherits its creator's cached origin in O(1); a non-created card maps to
    // itself (or its DeckVersion). Cleared each combat.
    private static readonly Dictionary<CardModel, CardModel> _cardOrigin = new();
    // Generated card model -> the tracking id of the source (card/power/potion/relic) that created it,
    // captured from whatever was executing at generation time. Distinct from the self-clone seam
    // (_cardCreatedBy): this covers cross-source generation (Shiv, etc.). The generated card keeps its own
    // row; its damage is also summed onto this creator's generated bucket. Cleared each combat.
    private static readonly Dictionary<CardModel, string> _cardGeneratedBy = new();
    // The IMMEDIATE generator (one step up the chain, before rolling up to the root) for each generated card
    // model. Parallel to _cardGeneratedBy; used to build the overlay's multi-level generation tree. Cleared
    // each combat alongside _cardGeneratedBy.
    private static readonly Dictionary<CardModel, string> _cardGeneratedByImmediate = new();
    // The tracking id each live card model was last registered under this combat. When a card's identity
    // changes mid-combat (upgrade/downgrade/enchant — Cunning Potion, Armaments, Drain Power, enemy
    // debuffs), its tracking id changes; this lets us migrate the old entry's stats onto the new id so the
    // card stays one row instead of splitting. Cleared each combat (ids are recomputed fresh per combat).
    private static readonly Dictionary<CardModel, string> _cardCurrentTrackingId = new();
    // Tracking id each deck card was marked removed under, keyed on the deck-master CardModel object.
    // Lets us revive the original entry when the SAME object returns to the deck under a new tracking
    // id (e.g. a Thieving Hopper steal/return, where the game rewrites FloorAddedToDeck to the current
    // floor and so changes the id). Persists across combats within a run; cleared only on ResetRun
    // because the stolen card is typically returned mid-combat but the deck is re-scanned afterward.
    private static readonly Dictionary<CardModel, string> _removedDeckCardIds = new();

    // Cards that always share one tracking entry regardless of how many instances are generated mid-combat.
    private static readonly HashSet<string> SingletonCardIds = new() { "SOVEREIGN_BLADE" };

    // Caches resolved Steam names by NetId so we don't query the platform on every room/combat.
    // Only successful resolutions are cached, so a not-yet-available name is retried next time.
    private static readonly Dictionary<string, string> _steamNameCache = new();

    // Tracks the card currently being played
    private static readonly AsyncLocal<CardModel?> _currentPlayingCard = new();
    
    // Cards added to hand during a play (to wait for enchantments)
    private static readonly AsyncLocal<List<CardModel>?> _deferredDraws = new();

    public static CardModel? CurrentPlayingCard
    {
        get
        {
            return _currentPlayingCard.Value;
        }
    }

    public static void StartCardPlay(CardModel card)
    {
        _currentPlayingCard.Value = card;
        _deferredDraws.Value = new List<CardModel>();
        Log.Debug($"StartCardPlay. Card: {card.Id.Entry}");
    }

    public static void EndCardPlay()
    {
        Log.Debug("EndCardPlay.");
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _currentPlayingCard.Value = null;
    }

    public static bool IsCardPlayActive()
    {
        return _currentPlayingCard.Value != null;
    }

    // Potion uses open the same draw-deferral window a card play does, so cards a potion creates — and
    // upgrades as part of the same action (e.g. Cunning Potion's upgraded Shivs) — are registered once the
    // potion fully resolves, at their final identity, instead of at their initial (un-upgraded) state.
    private static readonly AsyncLocal<bool> _potionUseDeferringDraws = new();

    public static void StartPotionUse()
    {
        if (_deferredDraws.Value != null)
        {
            return; // a card-play deferral is already open; don't clobber it
        }
        _deferredDraws.Value = new List<CardModel>();
        _potionUseDeferringDraws.Value = true;
    }

    public static void EndPotionUse()
    {
        if (!_potionUseDeferringDraws.Value)
        {
            return;
        }
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _potionUseDeferringDraws.Value = false;
    }

    // True while any draw-deferral window is open (card play or potion use).
    public static bool IsDeferringDraws()
    {
        return _deferredDraws.Value != null;
    }

    public static void ClearStateForTarget(Creature target)
    {
        lock (SyncRoot)
        {
            Log.Debug($"ClearStateForTarget. Target: {target.Name}");
            PoisonShares.Remove(target);
            foreach (var tracker in TargetedTrackers.Values)
            {
                tracker.ClearTarget(target);
            }
        }
    }
    
    public static void DeferDraw(CardModel card)
    {
        _deferredDraws.Value?.Add(card);
    }

    private static void ProcessDeferredDraws()
    {
        if (_deferredDraws.Value == null)
        {
            return;
        }
        
        foreach (var card in _deferredDraws.Value)
        {
            Log.Debug($"ProcessDeferredDraws. Registering deferred draw: {card.Id.Entry}");
            RegisterCard(card);
            AddDraw(card);
        }
        _deferredDraws.Value.Clear();
    }

    // One save file per run, keyed by run seed, so different runs (other profiles / multiplayer lobbies)
    // can each be resumed instead of only the most recent. Growth is bounded by MaxStoredRuns (LRU).
    private static readonly string SaveDirectory = ProjectSettings.GlobalizePath("user://deck_tracker_saves/");
    private const int MaxStoredRuns = 5;

    // Set by Publish() on any ledger mutation; read+cleared once per frame by DrainPendingSnapshot so the
    // expensive ledger clone happens at most once per rendered frame instead of once per damage event.
    private static volatile bool _publishPending;

    // --- Persistence Logic ---

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
                RunLogRecorder.BeginRun(runSeed, ExtractCharacterLabel(liveRun), liveRun?.AscensionLevel ?? 0, FirstPlayerGold(liveRun));
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
                    Relics = EntityLedger.Values.OfType<RelicStats>()
                        .ToDictionary(s => "RELIC_" + s.Id, s => (RelicStats)s.Clone())
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

    // Resolves the stable per-player owner key for a card: the owning player's NetId (Steam id) as a string.
    // NetId is globally unique and permanent, so a card's identity survives save/load and rejoins regardless
    // of join order. Must be deterministic and never throw — resolved from the same sourceCard used to build
    // the rest of the ID so deck scans and combat always agree. Returns UnknownOwnerKey for unowned cards.
    private static string ResolveOwnerNetId(CardModel? sourceCard)
    {
        try
        {
            var player = sourceCard?.Owner;
            if (player != null)
            {
                return player.NetId.ToString();
            }
        }
        catch (Exception e)
        {
            Log.Error($"ResolveOwnerNetId failed: {e.Message}");
        }
        return UnknownOwnerKey;
    }

    // Cosmetic only: maps a resolved owner NetId to its current ordered player index for row colour/ordering.
    private static int ResolvePlayerIndex(string ownerNetId)
    {
        return _playerIndexByNetId.TryGetValue(ownerNetId, out var idx) ? idx : 0;
    }

    // Clears the per-scan copy-index state. Called once at the start of a full deck scan so deck copy indices
    // are recomputed deterministically (and stale CardModel references from a prior room are dropped).
    public static void BeginDeckScan()
    {
        lock (SyncRoot)
        {
            _cardInstanceIds.Clear();
            _deckScanOrdinals.Clear();
        }
    }

    private static int GetCopyIndex(CardModel sourceCard)
    {
        if (SingletonCardIds.Contains(sourceCard.Id.Entry ?? "Unknown"))
        {
            return 0;
        }
        return _cardInstanceIds.TryGetValue(sourceCard, out var idx) ? idx : 0;
    }

    // Resolves the deck card a transient copy should be attributed to, caching the result so we never
    // re-walk. CreateClone() nulls a clone's DeckVersion but records CloneOf, so we auto-fill the shared
    // _cardCreatedBy map from it; a card-typed creator resolves to ITS origin (one cached lookup). A card
    // with no creator maps to itself, so purely generated cards (SHIV, etc.) keep their current behaviour
    // until the Shiv follow-up sets _cardCreatedBy to a non-card source. Locks internally (SyncRoot is
    // reentrant) since callers like AddDamage invoke this outside their ledger lock.
    private static CardModel ResolveSourceCard(CardModel card)
    {
        lock (SyncRoot)
        {
            if (_cardOrigin.TryGetValue(card, out var cached))
            {
                return cached;
            }
            // Auto-fill the shared creator seam from the game's clone link (Anger).
            if (!_cardCreatedBy.ContainsKey(card) && card.CloneOf != null)
            {
                _cardCreatedBy[card] = card.CloneOf;
            }
            CardModel origin;
            if (_cardCreatedBy.TryGetValue(card, out var creator) && creator is CardModel creatorCard)
            {
                origin = ResolveSourceCard(creatorCard); // creator is virtually always already cached
                Log.VeryDebug($"ResolveSourceCard. Card: {card.Id.Entry} rolled up to origin: {origin.Id.Entry}");
            }
            else
            {
                origin = card.DeckVersion ?? card;
            }
            _cardOrigin[card] = origin;
            return origin;
        }
    }

    // Eagerly captures the generator link the moment a generated card is created into ANY pile, while the
    // generating source is still executing. Cards that overflow a full hand are created straight into the
    // discard pile and never enter Hand here, so they would otherwise only be seen — with no executing
    // context — when later drawn. No-op for non-generated cards, status cards (enemy-added Wound/Dazed/...),
    // and cards already tracked/tagged. Stays silent when no source is executing: RegisterCard warns later.
    public static void TagGeneratedCardOnCreation(CardModel card)
    {
        if (card.FloorAddedToDeck != null || IsStatusCard(card) || IsGenerationAttributionExcluded(card))
        {
            return;
        }
        lock (SyncRoot)
        {
            if (_cardGeneratedBy.ContainsKey(card) || _cardInstanceIds.ContainsKey(card))
            {
                return;
            }
            TryApplyGeneratorTag(card);
        }
    }

    // Tags a generated card to whatever source is currently executing. Called at registration (warns when
    // nothing is executing — a likely sign a relic/power generator needs wrapping) and eagerly at creation
    // (silent). Must be called under SyncRoot.
    private static void TryTagGeneratedCard(CardModel card)
    {
        if (TryApplyGeneratorTag(card) || _cardGeneratedBy.ContainsKey(card))
        {
            return;
        }
        Log.Warn($"TryTagGeneratedCard. Card: {card.Id.Entry} generated with no tracked source; falling back to GEN (a relic/power generator may need wrapping).");
    }

    // Attributes a generated card to the source currently executing, in priority order: the card being
    // played -> executing relic -> playing potion -> executing power. The card branch is first and uses the
    // innermost play, so a force-played generator (Mayhem/Cascade auto-playing a Shiv-maker) gets credit
    // over the card/power that played it. Tags the chain root so a generated card that itself generates
    // rolls all the way up to the original creator. Returns true when a tag was applied (or already
    // present); false when no source is executing. Must be called under SyncRoot.
    private static bool TryApplyGeneratorTag(CardModel card)
    {
        if (_cardGeneratedBy.ContainsKey(card))
        {
            return true;
        }

        // A card playing itself is never a generation event (it is being played, not created).
        if (CurrentPlayingCard == card)
        {
            return false;
        }

        string? generatorId;
        if (CurrentPlayingCard != null)
        {
            generatorId = GetTrackingId(CurrentPlayingCard);
        }
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value))
        {
            generatorId = "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
        }
        else if (!string.IsNullOrEmpty(CurrentPlayingPotionId))
        {
            generatorId = CurrentPlayingPotionId;
        }
        else
        {
            // A relic/power only sets an executing id while a method we've wrapped runs, so this branch is
            // the implicit opt-in for those generators (cards and potions always have a context).
            generatorId = InstancedTracker.ExecutingSourceId;
        }

        if (string.IsNullOrEmpty(generatorId))
        {
            return false;
        }

        // Credit the ROOT generator: if the immediate generator is itself a generated card, walk up the
        // chain so the original creator accumulates the whole subtree's damage. Resolved once here and
        // cached in _cardGeneratedBy, so RouteGeneratedDamage never re-walks per damage event.
        var rootGeneratorId = ResolveRootGenerator(generatorId);
        _cardGeneratedBy[card] = rootGeneratorId;
        _cardGeneratedByImmediate[card] = generatorId;
        Log.Debug($"TryApplyGeneratorTag. Card: {card.Id.Entry} tagged to generator: {rootGeneratorId} (immediate: {generatorId})");
        return true;
    }

    // Wound/Dazed/Burn and other enemy-added status cards are not player-generated and are hidden from the
    // UI, so they are never attributed to a player source. Matches the UI's "Status" filter.
    private static bool IsStatusCard(CardModel card) => card.Type.ToString() == "Status";

    // Cards with bespoke damage/forge tracking (Sovereign Blade) must not be attributed as generated cards;
    // they keep their own row and custom forge distribution. SingletonCardIds is exactly these cards and
    // notably excludes SHIV, so genuine generated-card attribution is unaffected.
    private static bool IsGenerationAttributionExcluded(CardModel card) =>
        SingletonCardIds.Contains(card.Id.Entry ?? "");

    // When a card's identity (upgrade/enchant) changes mid-combat its tracking id changes, so its stats
    // would split across two ledger entries (e.g. the draw on the old upgrade, the play on the new). This
    // moves the previous entry's accumulated stats onto the new id so one row tracks the card across
    // mid-combat upgrades AND downgrades. Must be called under SyncRoot.
    private static void MigrateStatsOnIdentityChange(CardModel card, string newTrackingId)
    {
        if (!_cardCurrentTrackingId.TryGetValue(card, out var oldTrackingId) || oldTrackingId == newTrackingId)
        {
            _cardCurrentTrackingId[card] = newTrackingId;
            return;
        }
        _cardCurrentTrackingId[card] = newTrackingId;

        if (!EntityLedger.TryGetValue(oldTrackingId, out var oldEntity) || oldEntity is not CardStats oldStat)
        {
            return;
        }

        // Carry the per-combat encounter-seen flag so re-registration under the new id doesn't double-count.
        if (_incrementedThisCombat.Remove(oldTrackingId))
        {
            _incrementedThisCombat.Add(newTrackingId);
        }

        if (EntityLedger.TryGetValue(newTrackingId, out var newEntity) && newEntity is CardStats newStat)
        {
            MergeCardStats(newStat, oldStat);
            EntityLedger.Remove(oldTrackingId);
            Log.Debug($"MigrateStatsOnIdentityChange. Merged {oldTrackingId} into existing {newTrackingId}");
            return;
        }

        // Re-key the old entry under the new id and refresh the identity fields the id encodes.
        EntityLedger.Remove(oldTrackingId);
        oldStat.Id = newTrackingId;
        oldStat.UpgradeLevel = card.CurrentUpgradeLevel;
        oldStat.Enchantment = card.Enchantment?.Id.Entry ?? "";
        oldStat.BaseCardKey = GetBaseCardKey(card);
        oldStat.DisplayName = card.Title ?? card.Id.Entry ?? oldStat.DisplayName;
        EntityLedger[newTrackingId] = oldStat;
        Log.Debug($"MigrateStatsOnIdentityChange. Renamed {oldTrackingId} -> {newTrackingId}");
    }

    // A deck-master object that was previously removed has reappeared in the deck under a new tracking
    // id (its FloorAddedToDeck was rewritten on re-add, e.g. Thieving Hopper return). Re-key its
    // original entry onto the new id so its accumulated stats survive as one row, preserving the
    // original FloorAdded/BaseCardKey so the overlay still shows the true add-floor and merges with
    // any un-stolen siblings. Must be called under SyncRoot.
    private static void TryReviveReturnedDeckCard(CardModel sourceCard, string newTrackingId)
    {
        if (!_removedDeckCardIds.TryGetValue(sourceCard, out var oldTrackingId))
        {
            return;
        }
        _removedDeckCardIds.Remove(sourceCard);
        if (oldTrackingId == newTrackingId)
        {
            return; // same id -> SyncDeckState's refresh branch already clears the removal markers
        }
        if (!EntityLedger.TryGetValue(oldTrackingId, out var oldEntity) || oldEntity is not CardStats oldStat)
        {
            return;
        }

        // A separate entry already exists under the new id (rare id collision) -> fold stats in, but keep
        // the original add-floor identity on the survivor.
        if (EntityLedger.TryGetValue(newTrackingId, out var newEntity) && newEntity is CardStats newStat)
        {
            MergeCardStats(newStat, oldStat);
            newStat.FloorAdded = oldStat.FloorAdded;
            newStat.BaseCardKey = oldStat.BaseCardKey;
            EntityLedger.Remove(oldTrackingId);
            Log.Debug($"TryReviveReturnedDeckCard. Merged {oldTrackingId} into {newTrackingId}");
            return;
        }

        // Re-key the removed entry onto the new id and clear the removal markers. FloorAdded and
        // BaseCardKey are intentionally left at their original (pre-theft) values.
        EntityLedger.Remove(oldTrackingId);
        oldStat.Id = newTrackingId;
        oldStat.FloorRemoved = -1;
        oldStat.FloorLeftDeck = -1;
        oldStat.IsActive = true;
        oldStat.CopiesInDeck = 1; // SyncDeckState re-counts copies immediately after this scan
        EntityLedger[newTrackingId] = oldStat;
        Log.Debug($"TryReviveReturnedDeckCard. Revived {oldTrackingId} -> {newTrackingId} (FloorAdded kept {oldStat.FloorAdded})");
    }

    // Folds one card's accumulated stats into another (used when a mid-combat identity change lands on an
    // id that already exists). Must be called under SyncRoot.
    private static void MergeCardStats(CardStats target, CardStats source)
    {
        target.CombatDamage += source.CombatDamage;
        target.RunDamage += source.RunDamage;
        target.GeneratedCombatDamage += source.GeneratedCombatDamage;
        target.GeneratedRunDamage += source.GeneratedRunDamage;
        target.CombatTimesDrawn += source.CombatTimesDrawn;
        target.CombatTimesPlayed += source.CombatTimesPlayed;
        target.RawForgeCombat += source.RawForgeCombat;
        target.ConnectedForgeCombat += source.ConnectedForgeCombat;
        target.ReceivedForgeCombat += source.ReceivedForgeCombat;
        target.Act1.Add(source.Act1);
        target.Act2.Add(source.Act2);
        target.Act3.Add(source.Act3);
        target.Act4.Add(source.Act4);
    }

    // Walks GeneratedById links through the ledger up to the first non-generated source, so the root
    // generator gets credit for a chain of generated cards (G -> A -> B all credit G). Bounded loop guards
    // against any accidental cycle. Must be called under SyncRoot.
    private static string ResolveRootGenerator(string generatorId)
    {
        var current = generatorId;
        for (var guard = 0; guard < 16; guard++)
        {
            if (!EntityLedger.TryGetValue(current, out var entity)
                || entity is not CardStats stat
                || string.IsNullOrEmpty(stat.GeneratedById)
                || stat.GeneratedById == current)
            {
                break;
            }
            current = stat.GeneratedById;
        }
        return current;
    }

    // Deck cards: deterministic ordinal per (owner, identity), recomputed each scan so the id set is always
    // C0..C(N-1) for N copies — stable across client re-syncs (no churn-induced duplicate/EVOLVED rows).
    private static void AssignDeckCopyIndex(CardModel sourceCard, string ownerNetId)
    {
        var baseId = sourceCard.Id.Entry ?? "Unknown";
        if (SingletonCardIds.Contains(baseId))
        {
            _cardInstanceIds[sourceCard] = 0;
            return;
        }
        var floor = sourceCard.FloorAddedToDeck ?? 0;
        var upgrade = sourceCard.CurrentUpgradeLevel;
        var enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        var key = $"{ownerNetId}|{baseId}_F{floor}_U{upgrade}_{enchant}";
        _deckScanOrdinals.TryGetValue(key, out var ordinal);
        _cardInstanceIds[sourceCard] = ordinal;
        _deckScanOrdinals[key] = ordinal + 1;
    }

    // Generated/combat cards (not part of a deck scan): assign the next copy index from a persistent
    // per-{baseId}_F{floor} counter so each generated copy is a distinct entry (auto-stacked in the UI).
    private static int GetOrAssignCopyIndex(CardModel sourceCard)
    {
        if (_cardInstanceIds.TryGetValue(sourceCard, out var existing))
        {
            return existing;
        }
        var baseId = sourceCard.Id.Entry ?? "Unknown";
        if (SingletonCardIds.Contains(baseId))
        {
            _cardInstanceIds[sourceCard] = 0;
            return 0;
        }
        var floor = sourceCard.FloorAddedToDeck ?? 0;
        var key = $"{baseId}_F{floor}";
        _cardInstanceCounters.TryGetValue(key, out var counter);
        _cardInstanceIds[sourceCard] = counter;
        _cardInstanceCounters[key] = counter + 1;
        return counter;
    }

    // Identity is keyed by owning player's NetId instead of a per-physical-copy index, so it is a pure
    // function of deck composition + owner and stays stable across multiplayer client re-syncs.
    // ownerNetId may be passed by the deck scan (which knows the player); otherwise it is resolved from the card.
    public static string GetTrackingId(CardModel? card, string? ownerNetId = null)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = ResolveSourceCard(card);

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        int copyIndex = GetCopyIndex(sourceCard);
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);

        return $"{baseId}_F{floorAdded}_C{copyIndex}_U{upgradeLevel}_{enchant}_P{owner}";
    }

    public static string GetBaseCardKey(CardModel? card, string? ownerNetId = null)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = ResolveSourceCard(card);
        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);
        return $"{baseId}_F{floorAdded}_P{owner}";
    }

    // Resolves the active source ID: card first, then executing relic, then active potion, then fallback.
    public static string GetCurrentSourceId(CardModel? cardSource = null, string fallback = "External_Source")
    {
        if (cardSource != null) return GetTrackingId(cardSource);
        if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value)) return "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
        if (!string.IsNullOrEmpty(CurrentPlayingPotionId)) return CurrentPlayingPotionId;
        return fallback;
    }

    // --- UNIFIED TRACKER REGISTRY ---
    
    public static readonly Dictionary<string, GenericDamageTracker> SimpleDamageTrackers = new()
    {
        { "FLAME_BARRIER_POWER", new GenericDamageTracker("FLAME_BARRIER_POWER") },
        { "JUGGERNAUT_POWER", new GenericDamageTracker("JUGGERNAUT_POWER") },
        { "HAUNT_POWER", new GenericDamageTracker("HAUNT_POWER") },
        { "SPEEDSTER_POWER", new GenericDamageTracker("SPEEDSTER_POWER") },
        { "THUNDER_POWER", new GenericDamageTracker("THUNDER_POWER") },
        { "HAILSTORM_POWER", new GenericDamageTracker("HAILSTORM_POWER") },
        { "THORNS_POWER", new GenericDamageTracker("THORNS_POWER") },
        { "SERPENT_FORM_POWER", new GenericDamageTracker("SERPENT_FORM_POWER") },
        { "BLACK_HOLE_POWER", new GenericDamageTracker("BLACK_HOLE_POWER") },
        { "SLEIGHT_OF_FLESH_POWER", new GenericDamageTracker("SLEIGHT_OF_FLESH_POWER") },
        { "INFERNO_POWER", new GenericDamageTracker("INFERNO_POWER") },
        { "OUTBREAK_POWER", new GenericDamageTracker("OUTBREAK_POWER") },
        { "SMOKESTACK_POWER", new GenericDamageTracker("SMOKESTACK_POWER") },
        { "DEMISE_POWER", new GenericDamageTracker("DEMISE_POWER") },
    };

    public static readonly Dictionary<string, TargetedDamageTracker> TargetedTrackers = new()
    {
        { "STRANGLE_POWER", new TargetedDamageTracker("STRANGLE_POWER") },
        { "OBLIVION_POWER", new TargetedDamageTracker("OBLIVION_POWER") },
    };

    public static readonly Dictionary<string, BuffHandoffTracker> HandoffTrackers = new()
    {
        { "DEMON_FORM_POWER", new BuffHandoffTracker("DEMON_FORM_POWER", "DEMON_FORM_POWER", HandoffStrategy.ExactFifo) },
        { "ARSENAL_POWER", new BuffHandoffTracker("ARSENAL_POWER", "ARSENAL_POWER", HandoffStrategy.ExactFifo) },
        { "PREP_TIME_POWER", new BuffHandoffTracker("PREP_TIME_POWER", "PREP_TIME_POWER", HandoffStrategy.Proportional) },
        { "SHADOW_STEP_POWER", new BuffHandoffTracker("SHADOW_STEP_POWER", "SHADOW_STEP_POWER", HandoffStrategy.ExactFifo) },
        { "MONOLOGUE_POWER", new BuffHandoffTracker("MONOLOGUE_POWER", "MONOLOGUE_POWER", HandoffStrategy.ExactFifo) },
    };

    // Powers that apply poison or deal with Strength handoffs — must remain proportional so
    // RoutePoisonApplication and RouteStrengthApplication can find the executing tracker.
    public static readonly Dictionary<string, ProportionalShareTracker> ProportionalTrackers = new()
    {
        { "RUPTURE_POWER", new ProportionalShareTracker("RUPTURE_POWER") },
        { "CORROSIVE_WAVE_POWER", new ProportionalShareTracker("CORROSIVE_WAVE_POWER") },
        { "ENVENOM_POWER", new ProportionalShareTracker("ENVENOM_POWER") },
        { "NOXIOUS_FUMES_POWER", new ProportionalShareTracker("NOXIOUS_FUMES_POWER") },
    };

    public static readonly Dictionary<string, QueueBuilderTracker> QueueTrackers = new()
    {
        { "STORM_POWER", new QueueBuilderTracker("STORM_POWER", needsFlattening: true) },
        { "TRASH_TO_TREASURE_POWER", new QueueBuilderTracker("TRASH_TO_TREASURE_POWER", needsFlattening: true) },
        { "LIGHTNING_ROD_POWER", new QueueBuilderTracker("LIGHTNING_ROD_POWER") },
        { "SPINNER_POWER", new QueueBuilderTracker("SPINNER_POWER") },
    };

    public static readonly InstancedPowerTracker InstancedTracker = new();

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
            ResetInternalsCombat();
            RelicExecutionManager.ResetState();
            RelicNameCache.Clear();
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
    
    public static MegaCrit.Sts2.Core.Runs.RunState? GetLiveRunState()
    {
        var stateProperty = AccessTools.Property(typeof(MegaCrit.Sts2.Core.Runs.RunManager), "State");
        return stateProperty?.GetValue(MegaCrit.Sts2.Core.Runs.RunManager.Instance) as MegaCrit.Sts2.Core.Runs.RunState;
    }

    public static void SetPlayerLabel(int playerIndex, string label)
    {
        PlayerLabels[playerIndex] = label;
        Log.Debug($"SetPlayerLabel. Player {playerIndex}: {label}");
    }

    // Records a player's stable NetId -> ordered index mapping (used only for row colour/ordering).
    public static void SetPlayerIndexForNetId(string netId, int playerIndex)
    {
        _playerIndexByNetId[netId] = playerIndex;
    }

    public static string GetPlayerDisplayName(Player player)
    {
        var characterTitle = player.Character.Title.GetFormattedText();
        if (player.RunState.Players.Count <= 1)
        {
            return characterTitle;
        }

        var netKey = player.NetId.ToString();
        if (_steamNameCache.TryGetValue(netKey, out var cachedName))
        {
            return cachedName;
        }

        // The Steam name lookup can throw or return null on a client where a remote player's name
        // is not yet cached, or while NetService is still initialising. This runs inside synchronized
        // game hooks during co-op, so any throw here would desync the run — fall back to the character title.
        try
        {
            var netService = RunManager.Instance?.NetService;
            if (netService?.Platform == null)
            {
                Log.Warn("GetPlayerDisplayName. NetService unavailable, using character title.");
                return characterTitle;
            }

            var steamName = PlatformUtil.GetPlayerNameRaw(netService.Platform, player.NetId);
            if (string.IsNullOrEmpty(steamName))
            {
                return characterTitle;
            }

            _steamNameCache[netKey] = steamName;
            return steamName;
        }
        catch (Exception e)
        {
            Log.Error($"GetPlayerDisplayName failed, using character title: {e.Message}");
            return characterTitle;
        }
    }

    private static void RestoreLiveInstances()
    {
        var run = GetLiveRunState();
        if (run == null)
        {
            return;
        }

        BeginDeckScan();
        for (var playerIdx = 0; playerIdx < run.Players.Count; playerIdx++)
        {
            var player = run.Players[playerIdx];
            var netId = player.NetId.ToString();
            _playerIndexByNetId[netId] = playerIdx;
            PlayerLabels[playerIdx] = GetPlayerDisplayName(player);

            foreach (var relic in player.Relics)
            {
                RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
                var stats = GetOrCreateRelicStats(relic.Id.Entry);
                stats.Model = relic;
                stats.IsActive = true;
                stats.PlayerIndex = playerIdx;
            }

            foreach (var card in player.Deck.Cards)
            {
                RegisterCard(card, netId, isDeckScan: true);
            }

            for (var i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                if (potion == null)
                {
                    continue;
                }

                var existingId = PotionInstanceIds.FirstOrDefault(kvp => kvp.Key == potion).Value;

                if (string.IsNullOrEmpty(existingId))
                {
                    var idPrefix = $"POTION_{potion.Id.Entry}_";
                    existingId = EntityLedger.Values.OfType<PotionStats>()
                        .FirstOrDefault(p => p.Model == null && MatchesPotionType(p.Id, idPrefix)
                            && (p.OwnerNetId == netId || p.OwnerNetId == null))?.Id;

                    if (string.IsNullOrEmpty(existingId))
                    {
                        _potionCounter++;
                        existingId = $"POTION_{potion.Id.Entry}_{_potionCounter}";

                        string displayName = potion.Title?.GetFormattedText() ?? potion.Id.Entry ?? "Unknown Potion";
                        EntityLedger[existingId] = new PotionStats
                        {
                            Id = existingId,
                            DisplayName = displayName,
                            FloorObtained = 1,
                            OwnerNetId = netId,
                            IsActive = true
                        };
                    }
                    PotionInstanceIds[potion] = existingId;
                }

                var potionStat = (PotionStats)EntityLedger[existingId];
                potionStat.OwnerNetId ??= netId; // Backfill owner for saves made before owner tracking.
                potionStat.Model = potion;
                potionStat.PlayerIndex = playerIdx;
            }
        }
        Log.Debug("RestoreLiveInstances. Live object references restored.");
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
        FinalizeCombatExport();
        lock (SyncRoot)
        {
            ResetInternalsCombat();
        }
        SaveState();
    }

    // Snapshots the just-ended combat (each entity's contribution + the local player's HP/alive state) into
    // the run export log, which triggers the automatic JSON + CSV writes inside RunLogRecorder.EndCombat.
    private static void FinalizeCombatExport()
    {
        var contributions = BuildCombatEntityStats();
        var run = GetLiveRunState();
        var player = run != null && run.Players.Count > 0 ? run.Players[0] : null;
        var hpAfter = player?.Creature.CurrentHp ?? 0;
        var alive = player?.Creature.IsAlive ?? true;
        RunLogRecorder.EndCombat(hpAfter, alive, contributions);
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
        if (entity is CardStats)
        {
            return entity.CombatTimesDrawn > 0 || entity.CombatDamage > 0 || entity.GeneratedCombatDamage > 0;
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

    // Snapshots every player's master deck as DeckCardInfo for the export log's out-of-combat deck diff.
    // Must be called after a deck scan so tracking ids resolve consistently.
    public static List<DeckCardInfo> BuildDeckInfo(IRunState run)
    {
        var list = new List<DeckCardInfo>();
        lock (SyncRoot)
        {
            foreach (var player in run.Players)
            {
                var netId = player.NetId.ToString();
                foreach (var card in player.Deck.Cards)
                {
                    var id = GetTrackingId(card, netId);
                    list.Add(new DeckCardInfo
                    {
                        Id = id,
                        DisplayName = card.Title ?? card.Id.Entry ?? id,
                        BaseKey = GetBaseCardKey(card, netId)
                    });
                }
            }
        }
        return list;
    }
    
    public static void HandleRemove(CardModel card, int floorRemoved)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity) && entity is CardStats stat)
            {
                Log.Debug($"HandleRemove. Card: {uniqueTrackingId}");
                if (stat.CopiesInDeck > 1)
                {
                    stat.CopiesInDeck--;
                }
                else
                {
                    stat.FloorRemoved = floorRemoved;
                    stat.FloorLeftDeck = floorRemoved;
                    stat.IsActive = false;
                    stat.CopiesInDeck = 0;
                    // Remember the object under its removal id so we can revive this entry if the SAME card
                    // returns to the deck under a new id (e.g. Thieving Hopper rewrites FloorAddedToDeck).
                    _removedDeckCardIds[ResolveSourceCard(card)] = uniqueTrackingId;
                    Log.Debug($"HandleRemove. Recorded for revival. Card: {uniqueTrackingId}");
                }
            }
        }
        Publish();
    }
    
    public static void RegisterCard(CardModel card, string? ownerNetId = null, bool isDeckScan = false)
    {
        CardModel sourceCard = ResolveSourceCard(card);
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);

        lock (SyncRoot)
        {
            // Assign the copy index before building the id: deterministic ordinal for deck scans,
            // persistent counter for generated/combat cards not seen in the current scan.
            if (isDeckScan)
            {
                AssignDeckCopyIndex(sourceCard, owner);
            }
            else if (!_cardInstanceIds.ContainsKey(sourceCard))
            {
                GetOrAssignCopyIndex(sourceCard);
            }
        }

        string uniqueTrackingId = GetTrackingId(card, owner);

        lock (SyncRoot)
        {
            bool isGenerated = card.FloorAddedToDeck == null;
            if (isGenerated && !IsStatusCard(card) && !IsGenerationAttributionExcluded(card))
            {
                TryTagGeneratedCard(card);
            }
            // Deck scans recompute ids deterministically and rely on the existing version-merge UI, so only
            // live draw/play registrations migrate a card whose identity changed mid-combat.
            if (!isDeckScan)
            {
                MigrateStatsOnIdentityChange(card, uniqueTrackingId);
            }
            else
            {
                TryReviveReturnedDeckCard(sourceCard, uniqueTrackingId);
            }
            if (!EntityLedger.TryGetValue(uniqueTrackingId, out var existing) || existing is not CardStats stat)
            {
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                Log.Debug($"RegisterCard. NEW Card: {uniqueTrackingId}, Generated: {isGenerated}");
                stat = new CardStats
                {
                    Id = uniqueTrackingId,
                    DisplayName = displayName,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    UpgradeLevel = sourceCard.CurrentUpgradeLevel,
                    BaseCardKey = GetBaseCardKey(card, owner),
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1,
                    IsActive = !isGenerated,
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                EntityLedger[uniqueTrackingId] = stat;
            }

            stat.PlayerIndex = ResolvePlayerIndex(owner);
            stat.Model = card;
            if (_cardGeneratedBy.TryGetValue(card, out var generatorId))
            {
                stat.GeneratedById = generatorId;
            }
            if (_cardGeneratedByImmediate.TryGetValue(card, out var immediateGeneratorId))
            {
                stat.GeneratedByImmediateId = immediateGeneratorId;
            }

            if (_currentCombatType != "Unknown" && isGenerated && _incrementedThisCombat.Add(uniqueTrackingId))
            {
                stat.GetAct(_currentAct)?.AddEncounterSeen(_currentCombatType);
            }
        }
    }
    
    public static void AddDraw(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity))
            {
                var actData = entity.GetAct(_currentAct);
                if (actData != null) actData.TimesDrawn++;
                entity.CombatTimesDrawn++;
                Log.Debug($"AddDraw. Card: {uniqueTrackingId}");
            }
        }
        Publish();
    }

    // Registers a card and credits a draw in one step, used when a draw must be counted outside the
    // normal draw hooks (e.g. a card autoplayed without ever entering the hand). Done directly rather
    // than via the deferral window so the draw pairs with the play, which is also counted directly.
    public static void RegisterAndAddDraw(CardModel card)
    {
        RegisterCard(card);
        AddDraw(card);
        ForcePublish();
    }

    public static void AddPlay(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity))
            {
                var actData = entity.GetAct(_currentAct);
                if (actData != null) actData.TimesPlayed++;
                entity.CombatTimesPlayed++;
                Log.Debug($"AddPlay. Card: {uniqueTrackingId}");
            }
        }
        Publish();
    }
    
    public static void AddDamage(CardModel card, decimal amount)
    {
        // Generated-damage routing happens inside AddDamageById (keyed off the ledger entry's GeneratedById),
        // so every damage path — direct, poison, orbs, ... — credits the generator's bucket exactly once.
        AddDamageById(GetTrackingId(card), amount);
    }

    // If this card was generated by a known source, also credit the generator's separate generated-damage
    // bucket so the overlay can attribute the generated card's damage back to its creator. The generated
    // card still keeps its own direct-damage row; this is an additive second bucket, never a replacement.
    // Caller must hold SyncRoot. Works for ANY damage path because it reads the ledger entry's GeneratedById
    // (set at registration to the root generator) rather than the CardModel-keyed map.
    private static void RouteGeneratedDamage(string generatedTrackingId, decimal amount)
    {
        if (amount <= 0
            || !EntityLedger.TryGetValue(generatedTrackingId, out var entity)
            || entity is not CardStats stat
            || string.IsNullOrEmpty(stat.GeneratedById))
        {
            return;
        }
        if (!EntityLedger.TryGetValue(stat.GeneratedById, out var generator))
        {
            Log.Warn($"RouteGeneratedDamage. Generator {stat.GeneratedById} for {generatedTrackingId} not in ledger; generated damage {amount} dropped.");
            return;
        }
        generator.AddGeneratedDamage(amount, _currentAct, _currentCombatType);
        Log.VeryDebug($"RouteGeneratedDamage. Id: {generatedTrackingId}, Amount: {amount}, Generator: {stat.GeneratedById}");
    }

    public static void AddDamageById(string trackingId, decimal amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(trackingId))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(trackingId, out var entity))
            {
                entity.AddCombatDamage(amount, _currentAct, _currentCombatType);
                Log.Debug($"AddDamageById. Amount: {amount}, ID: {trackingId}");
                RouteGeneratedDamage(trackingId, amount);
            }
        }
        Publish();
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