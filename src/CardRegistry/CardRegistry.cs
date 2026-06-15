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
    // The tracking id each live card model was last registered under this combat. When a card's identity
    // changes mid-combat (upgrade/downgrade/enchant — Cunning Potion, Armaments, Drain Power, enemy
    // debuffs), its tracking id changes; this lets us migrate the old entry's stats onto the new id so the
    // card stays one row instead of splitting. Cleared each combat (ids are recomputed fresh per combat).
    private static readonly Dictionary<CardModel, string> _cardCurrentTrackingId = new();

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

    private static readonly string SavePath = ProjectSettings.GlobalizePath("user://deck_tracker_save.json");

    public static event Action<List<CardStats>>? Changed;

    // --- Persistence Logic ---

    public static void SyncRun(string runSeed)
    {
        if (string.IsNullOrEmpty(runSeed))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_currentRunSeed == runSeed)
            {
                return;
            }

            _currentRunSeed = runSeed;

            if (TryLoadState(runSeed))
            {
                Log.Info($"SyncRun. Resumed run data for seed: {runSeed}");
            }
            else
            {
                Log.Info($"SyncRun. Starting fresh tracker for seed: {runSeed}");
                ResetRun();
            }
            RestoreLiveInstances();
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

            string json = JsonSerializer.Serialize(state, SavedStateCtx.Default.SavedRunState);
            System.IO.File.WriteAllText(SavePath, json);
            Log.Info("SaveState. State saved successfully.");
        }
        catch (Exception e)
        {
            Log.Error($"SaveState Failed: {e.Message}");
        }
    }

    private static bool TryLoadState(string targetSeed)
    {
        try
        {
            if (!System.IO.File.Exists(SavePath))
            {
                Log.Debug("TryLoadState. No save file found.");
                return false;
            }

            string json = System.IO.File.ReadAllText(SavePath);
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
            _playerIndexByNetId.Clear();
            PlayerLabels.Clear();
            _steamNameCache.Clear();
            Log.Info("ResetRun. Run state cleared.");
        }
        Publish();
    }
    
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
        lock (SyncRoot)
        {
            ResetInternalsCombat();
        }
        SaveState();
        Publish();
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
        var uniqueTrackingId = GetTrackingId(card);
        AddDamageById(uniqueTrackingId, amount);
        RouteGeneratedDamage(card, amount);
    }

    // If this card was generated by a known source, also credit the generator's separate generated-damage
    // bucket so the overlay can attribute the generated card's damage back to its creator. The generated
    // card still keeps its own direct-damage row; this is an additive second bucket, never a replacement.
    private static void RouteGeneratedDamage(CardModel card, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (!_cardGeneratedBy.TryGetValue(card, out var generatorId) || string.IsNullOrEmpty(generatorId))
            {
                return;
            }
            if (!EntityLedger.TryGetValue(generatorId, out var generator))
            {
                Log.Warn($"RouteGeneratedDamage. Generator {generatorId} for {card.Id.Entry} not in ledger; generated damage {amount} dropped.");
                return;
            }
            generator.AddGeneratedDamage(amount, _currentAct, _currentCombatType);
            Log.VeryDebug($"RouteGeneratedDamage. Card: {card.Id.Entry}, Amount: {amount}, Generator: {generatorId}");
        }
        Publish();
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
            }
        }
        Publish();
    }
    
    public static void ForcePublish()
    {
        Publish();
    }

    private static void Publish()
    {
        List<CardStats> statsCopy;
        lock (SyncRoot)
        {
            statsCopy = EntityLedger.Values.OfType<CardStats>().Select(s => (CardStats)s.Clone()).ToList();
        }
        Changed?.Invoke(statsCopy);
    }
}