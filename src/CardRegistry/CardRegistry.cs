using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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

    private static readonly Dictionary<CardModel, int> _cardInstanceIds = new();
    private static readonly Dictionary<string, int> _cardInstanceCounters = new();

    // Maps player index (order in IRunState.Players) to the character's display name
    public static readonly Dictionary<int, string> PlayerLabels = new();

    // Caches resolved Steam names by NetId so we don't query the platform on every room/combat.
    // Only successful resolutions are cached, so a not-yet-available name is retried next time.
    private static readonly Dictionary<string, string> _steamNameCache = new();

    // Cards that always share one tracking entry regardless of how many instances are generated mid-combat
    private static readonly HashSet<string> SingletonCardIds = new() { "SOVEREIGN_BLADE" };

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
        GD.Print($"[DeckTracker] StartCardPlay. Card: {card.Id.Entry}");
    }

    public static void EndCardPlay()
    {
        GD.Print("[DeckTracker] EndCardPlay.");
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _currentPlayingCard.Value = null;
    }

    public static bool IsCardPlayActive()
    {
        return _currentPlayingCard.Value != null;
    }
    
    public static void ClearStateForTarget(Creature target)
    {
        lock (SyncRoot)
        {
            GD.Print($"[DeckTracker] ClearStateForTarget. Target: {target.Name}");
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
            GD.Print($"[DeckTracker] ProcessDeferredDraws. Registering deferred draw: {card.Id.Entry}");
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
                GD.Print($"[DeckTracker] SyncRun. Resumed run data for seed: {runSeed}");
            }
            else
            {
                GD.Print($"[DeckTracker] SyncRun. Starting fresh tracker for seed: {runSeed}");
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
            GD.Print("[DeckTracker] SaveState. State saved successfully.");
        }
        catch (Exception e)
        {
            GD.PrintErr($"[DeckTracker] SaveState Failed: {e.Message}");
        }
    }

    private static bool TryLoadState(string targetSeed)
    {
        try
        {
            if (!System.IO.File.Exists(SavePath))
            {
                GD.Print("[DeckTracker] TryLoadState. No save file found.");
                return false;
            }

            string json = System.IO.File.ReadAllText(SavePath);
            SavedRunState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedRunState);

            if (state == null || state.RunSeed != targetSeed)
            {
                GD.Print($"[DeckTracker] TryLoadState. Seed mismatch or null state. Expected: {targetSeed}, Got: {state?.RunSeed}");
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
            GD.PrintErr($"[DeckTracker] TryLoadState Failed: {e.Message}");
            return false;
        }
    }

    // Assigns a per-run copy index to each distinct physical CardModel object.
    // On post-load re-association, scans EntityLedger for a matching saved entry not yet claimed by a live card.
    private static int GetOrAssignCopyIndex(CardModel sourceCard)
    {
        if (_cardInstanceIds.TryGetValue(sourceCard, out var existing))
        {
            return existing;
        }

        var baseId = sourceCard.Id.Entry ?? "Unknown";

        // Singleton cards always share copy index 0 — all instances map to the same tracking entry
        if (SingletonCardIds.Contains(baseId))
        {
            _cardInstanceIds[sourceCard] = 0;
            GD.Print($"[DeckTracker] GetOrAssignCopyIndex. Singleton card {baseId}, assigning CopyIndex: 0");
            return 0;
        }
        var floor = sourceCard.FloorAddedToDeck ?? 0;
        var upgrade = sourceCard.CurrentUpgradeLevel;
        var enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        var prefix = $"{baseId}_F{floor}_C";
        var suffix = $"_U{upgrade}_{enchant}";

        foreach (var kvp in EntityLedger)
        {
            if (!kvp.Key.StartsWith(prefix) || !kvp.Key.EndsWith(suffix)) continue;
            if (!kvp.Value.IsActive || kvp.Value.Model != null) continue;

            var middle = kvp.Key[prefix.Length..^suffix.Length];
            if (!int.TryParse(middle, out var savedIdx)) continue;

            kvp.Value.Model = sourceCard;
            _cardInstanceIds[sourceCard] = savedIdx;
            var basePrefix = $"{baseId}_F{floor}";
            _cardInstanceCounters.TryGetValue(basePrefix, out var cur);
            if (savedIdx >= cur) _cardInstanceCounters[basePrefix] = savedIdx + 1;
            GD.Print($"[DeckTracker] GetOrAssignCopyIndex. Re-associated: {kvp.Key}, CopyIndex: {savedIdx}");
            return savedIdx;
        }

        var pref = $"{baseId}_F{floor}";
        _cardInstanceCounters.TryGetValue(pref, out var counter);
        _cardInstanceIds[sourceCard] = counter;
        _cardInstanceCounters[pref] = counter + 1;
        GD.Print($"[DeckTracker] GetOrAssignCopyIndex. Assigned new CopyIndex: {counter} for {baseId}_F{floor}");
        return counter;
    }

    public static string GetTrackingId(CardModel? card)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = card.DeckVersion ?? card;

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        int copyIndex = _cardInstanceIds.TryGetValue(sourceCard, out var idx) ? idx : 0;

        return $"{baseId}_F{floorAdded}_C{copyIndex}_U{upgradeLevel}_{enchant}";
    }

    public static string GetBaseCardKey(CardModel? card)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = card.DeckVersion ?? card;
        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int copyIndex = _cardInstanceIds.TryGetValue(sourceCard, out var idx) ? idx : 0;
        return $"{baseId}_F{floorAdded}_C{copyIndex}";
    }

    // Resolves the active source ID: card first, then executing relic, then active potion, then fallback.
    public static string GetCurrentSourceId(CardModel? cardSource = null, string fallback = "External_Source")
    {
        if (cardSource != null) return GetTrackingId(cardSource);
        if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value)) return "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value;
        if (CurrentPlayingPotion != null && PotionInstanceIds.TryGetValue(CurrentPlayingPotion, out var potId)) return potId;
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
            GD.Print("[DeckTracker] ResetInternalsCombat. All state reset.");
        }
    }
    
    public static void ClearSession()
    {
        lock (SyncRoot)
        {
            _currentRunSeed = "";
            GD.Print("[DeckTracker] ClearSession. Session cleared.");
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
            PlayerLabels.Clear();
            _steamNameCache.Clear();
            GD.Print("[DeckTracker] ResetRun. Run state cleared.");
        }
        Publish();
    }
    
    public static void SyncDeckState(int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            var copyCounts = activeDeckIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            var uniqueActiveIds = new HashSet<string>(activeDeckIds);
            
            GD.Print($"[DeckTracker] SyncDeckState. Floor: {currentFloor}, Active Count: {activeDeckIds.Count}");
            foreach (var stat in EntityLedger.Values.OfType<CardStats>())
            {
                if (stat.IsActive && !uniqueActiveIds.Contains(stat.Id))
                {
                    GD.Print($"[DeckTracker]   -> {stat.Id} removed from deck");
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
        GD.Print($"[DeckTracker] SetPlayerLabel. Player {playerIndex}: {label}");
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
                GD.Print("[DeckTracker] GetPlayerDisplayName. NetService unavailable, using character title.");
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
            GD.PrintErr($"[DeckTracker] GetPlayerDisplayName failed, using character title: {e.Message}");
            return characterTitle;
        }
    }

    public static void SetCardPlayerIndex(string trackingId, int playerIndex)
    {
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(trackingId, out var entity) && entity is CardStats stat)
            {
                stat.PlayerIndex = playerIndex;
            }
        }
    }

    private static void RestoreLiveInstances()
    {
        var run = GetLiveRunState();
        if (run == null)
        {
            return;
        }

        for (var playerIdx = 0; playerIdx < run.Players.Count; playerIdx++)
        {
            var player = run.Players[playerIdx];
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
                RegisterCard(card);
                SetCardPlayerIndex(GetTrackingId(card), playerIdx);
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
                    existingId = EntityLedger.Values.OfType<PotionStats>()
                        .FirstOrDefault(p => p.Model == null && p.Id.Contains(potion.Id.Entry))?.Id;

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
                            IsActive = true
                        };
                    }
                    PotionInstanceIds[potion] = existingId;
                }
                ((PotionStats)EntityLedger[existingId]).Model = potion;
            }
        }
        GD.Print("[DeckTracker] RestoreLiveInstances. Live object references restored.");
    }
    
    public static void StartCombat(string combatType, int currentFloor, int currentAct, List<string> activeDeckIds)
    {
        SyncDeckState(currentFloor, activeDeckIds);

        lock (SyncRoot)
        {
            _currentAct = currentAct;
            _currentCombatType = combatType;
            GD.Print($"[DeckTracker] StartCombat. Type: {_currentCombatType}, Act: {_currentAct}");
            
            foreach (var entity in EntityLedger.Values)
            {
                entity.CombatDamage = 0;
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
        GD.Print("[DeckTracker] ProcessCombatEnd.");
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
                GD.Print($"[DeckTracker] HandleRemove. Card: {uniqueTrackingId}");
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
    
    public static void RegisterCard(CardModel card)
    {
        CardModel sourceCard = card.DeckVersion ?? card;

        lock (SyncRoot)
        {
            // Must assign copy index before GetTrackingId so the ID includes the correct _C{n} segment
            GetOrAssignCopyIndex(sourceCard);
        }

        string uniqueTrackingId = GetTrackingId(card);

        lock (SyncRoot)
        {
            bool isGenerated = card.FloorAddedToDeck == null;
            if (!EntityLedger.TryGetValue(uniqueTrackingId, out var existing) || existing is not CardStats stat)
            {
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                GD.Print($"[DeckTracker] RegisterCard. NEW Card: {uniqueTrackingId}, Generated: {isGenerated}");
                stat = new CardStats
                {
                    Id = uniqueTrackingId,
                    DisplayName = displayName,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    UpgradeLevel = sourceCard.CurrentUpgradeLevel,
                    BaseCardKey = GetBaseCardKey(card),
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1,
                    IsActive = !isGenerated,
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                EntityLedger[uniqueTrackingId] = stat;
            }

            stat.Model = card;

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
                GD.Print($"[DeckTracker] AddDraw. Card: {uniqueTrackingId}");
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
                GD.Print($"[DeckTracker] AddPlay. Card: {uniqueTrackingId}");
            }
        }
        Publish();
    }
    
    public static void AddDamage(CardModel card, decimal amount)
    {
        var uniqueTrackingId = GetTrackingId(card);
        AddDamageById(uniqueTrackingId, amount);
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
                GD.Print($"[DeckTracker] AddDamageById. Amount: {amount}, ID: {trackingId}");
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