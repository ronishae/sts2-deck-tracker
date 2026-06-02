using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly object SyncRoot = new();
    
    private static Dictionary<string, CardStats> Totals = new();
    private static string _currentRunSeed = "";
    
    private static int _currentAct = 1;
    private static string _currentCombatType = "Unknown";
    
    private static HashSet<string> _incrementedThisCombat = new();

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

    private static ActData? GetActData(EntityStats stat, int actNum)
    {
        switch (actNum)
        {
            case 1: return stat.Act1;
            case 2: return stat.Act2;
            case 3: return stat.Act3;
            case 4: return stat.Act4;
            default: return null;
        }
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
                    Totals = Totals.ToDictionary(kvp => kvp.Key, kvp => (CardStats)kvp.Value.Clone()),
                    Potions = PotionLedger.ToDictionary(kvp => kvp.Key, kvp => (PotionStats)kvp.Value.Clone())
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
                return false;
            }

            string json = System.IO.File.ReadAllText(SavePath);
            SavedRunState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedRunState);
            
            if (state == null || state.RunSeed != targetSeed)
            {
                return false;
            }

            Totals = state.Totals;
            PotionLedger = state.Potions ?? new Dictionary<string, PotionStats>();
            return true;
        }
        catch
        {
            return false;
        }
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

        return $"{baseId}_F{floorAdded}_U{upgradeLevel}_{enchant}";
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

    public static readonly Dictionary<string, ProportionalShareTracker> ProportionalTrackers = new()
    {
        { "INFERNO_POWER", new ProportionalShareTracker("INFERNO_POWER") },
        { "OUTBREAK_POWER", new ProportionalShareTracker("OUTBREAK_POWER") },
        { "SMOKESTACK_POWER", new ProportionalShareTracker("SMOKESTACK_POWER") },
        { "RUPTURE_POWER", new ProportionalShareTracker("RUPTURE_POWER") },
        { "CORROSIVE_WAVE_POWER", new ProportionalShareTracker("CORROSIVE_WAVE_POWER") },
        { "ENVENOM_POWER", new ProportionalShareTracker("ENVENOM_POWER") },
        { "NOXIOUS_FUMES_POWER", new ProportionalShareTracker("NOXIOUS_FUMES_POWER") },
        { "DEMISE_POWER", new ProportionalShareTracker("DEMISE_POWER") },
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
            ForgeHistory.Clear();
            BladeReplayModifierTracker.Clear();
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
            Totals.Clear();
            _currentAct = 1;
            ResetInternalsCombat();
            RelicLedger.Clear();
            RelicExecutionManager.ResetState();
            RelicNameCache.Clear();
            PotionLedger.Clear();
            PotionInstanceIds.Clear();
            _potionCounter = 0;
            GD.Print("[DeckTracker] ResetRun. Run state cleared.");
        }
        Publish();
    }
    
    public static void SyncDeckState(int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            var copyCounts = activeDeckIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            HashSet<string> uniqueActiveIds = new HashSet<string>(activeDeckIds);
            
            GD.Print($"[DeckTracker] SyncDeckState. Floor: {currentFloor}, Active Count: {activeDeckIds.Count}");
            foreach (var stat in Totals.Values)
            {
                if (stat.IsActive && !uniqueActiveIds.Contains(stat.Id))
                {
                    GD.Print($"[DeckTracker]   -> {stat.Id} removed from deck");
                    stat.IsActive = false;
                    stat.CopiesInDeck = 0;
                        
                    int floorLeft = Math.Max(1, currentFloor - 1);
                    if (stat.FloorRemoved == -1)
                    {
                        stat.FloorLeftDeck = floorLeft;
                    }
                }
                else if (copyCounts.TryGetValue(stat.Id, out int count))
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
        if (stateProperty != null)
        {
            return stateProperty.GetValue(MegaCrit.Sts2.Core.Runs.RunManager.Instance) as MegaCrit.Sts2.Core.Runs.RunState;
        }
        return null;
    }
    
    private static void RestoreLiveInstances()
    {
        var run = GetLiveRunState();
        if (run == null)
        {
            return;
        }

        foreach (var player in run.Players)
        {
            foreach (var relic in player.Relics)
            {
                RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
                var stats = GetOrCreateRelicStats(relic.Id.Entry);
                stats.Model = relic;
                stats.IsActive = true; 
            }

            foreach (var card in player.Deck.Cards)
            {
                string trackingId = GetTrackingId(card);
                if (Totals.TryGetValue(trackingId, out var cardStats))
                {
                    cardStats.Model = card;
                }
            }
            
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                if (potion == null)
                {
                    continue;
                }

                string? existingId = PotionInstanceIds.FirstOrDefault(kvp => kvp.Key == potion).Value;

                if (string.IsNullOrEmpty(existingId))
                {
                    existingId = PotionLedger.Values.FirstOrDefault(p => p.Model == null && p.Id.Contains(potion.Id.Entry))?.Id;
                    
                    if (string.IsNullOrEmpty(existingId))
                    {
                        _potionCounter++;
                        existingId = $"POTION_{potion.Id.Entry}_{_potionCounter}";
                        
                        string displayName = potion.Title?.GetFormattedText() ?? potion.Id.Entry ?? "Unknown Potion";
                        PotionLedger[existingId] = new PotionStats
                        {
                            Id = existingId,
                            DisplayName = displayName,
                            FloorObtained = 1,
                            IsActive = true
                        };
                    }
                    PotionInstanceIds[potion] = existingId;
                }
                PotionLedger[existingId].Model = potion;
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
            
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0;
                stat.RawForgeCombat = 0;
                stat.ConnectedForgeCombat = 0;
                stat.ReceivedForgeCombat = 0;
                
                if (!stat.IsActive)
                {
                    continue;
                }
                
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    if (combatType == "Elite") actData.EncountersSeenElite++;
                    else if (combatType == "Boss") actData.EncountersSeenBoss++;
                    else actData.EncountersSeenHallway++;
                }
                _incrementedThisCombat.Add(stat.Id);
            }

            foreach (var stat in RelicLedger.Values)
            {
                stat.CombatDamage = 0;
                stat.RawForgeCombat = 0;
                stat.ConnectedForgeCombat = 0;
                stat.ReceivedForgeCombat = 0;

                if (!stat.IsActive)
                {
                    continue;
                }

                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    if (combatType == "Elite") actData.EncountersSeenElite++;
                    else if (combatType == "Boss") actData.EncountersSeenBoss++;
                    else actData.EncountersSeenHallway++;
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
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
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
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
            bool isGenerated = card.FloorAddedToDeck == null;
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                CardModel sourceCard = card.DeckVersion ?? card;
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                GD.Print($"[DeckTracker] RegisterCard. NEW Card: {uniqueTrackingId}, Generated: {isGenerated}");
                stat = new CardStats 
                { 
                    Id = uniqueTrackingId, 
                    DisplayName = displayName,
                    Model = card,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1, 
                    IsActive = !isGenerated,
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                Totals[uniqueTrackingId] = stat;
            }
            
            if (_currentCombatType != "Unknown" && isGenerated)
            {
                if (_incrementedThisCombat.Add(uniqueTrackingId))
                {
                    var actData = GetActData(stat, _currentAct);
                    if (actData != null)
                    {
                        if (_currentCombatType == "Elite") actData.EncountersSeenElite++;
                        else if (_currentCombatType == "Boss") actData.EncountersSeenBoss++;
                        else actData.EncountersSeenHallway++;
                    }
                }
            }
        }
    }
    
    public static void AddDraw(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    actData.TimesDrawn++;
                }
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
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    actData.TimesPlayed++;
                }
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

        if (trackingId.StartsWith("RELIC_"))
        {
            string relicId = trackingId.Substring(6); 
            AddRelicDamage(relicId, amount);
            return;
        }
        
        if (trackingId.StartsWith("POTION_"))
        {
            lock (SyncRoot)
            {
                if (PotionLedger.TryGetValue(trackingId, out var stat))
                {
                    stat.CombatDamage += amount;
                    stat.RunDamage += amount;
                    GD.Print($"[DeckTracker] AddDamageById (Potion). Amount: {amount}, ID: {trackingId}");
                    
                    var actData = GetActData(stat, _currentAct);
                    if (actData != null)
                    {
                        switch (_currentCombatType)
                        {
                            case "Elite": actData.DamageElite += amount; break;
                            case "Boss": actData.DamageBoss += amount; break;
                            case "Hallway": actData.DamageHallway += amount; break;
                        }
                    }
                }
            }
            Publish();
            return;
        }
        
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(trackingId, out var stat))
            {
                stat.CombatDamage += amount;
                stat.RunDamage += amount;
                GD.Print($"[DeckTracker] AddDamageById. Amount: {amount}, Card: {trackingId}");
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    switch (_currentCombatType)
                    {
                        case "Elite": actData.DamageElite += amount; break;
                        case "Boss": actData.DamageBoss += amount; break;
                        case "Hallway": actData.DamageHallway += amount; break;
                    }
                }
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
            statsCopy = Totals.Values.Select(s => (CardStats)s.Clone()).ToList();
        }
        Changed?.Invoke(statsCopy);
    }
}