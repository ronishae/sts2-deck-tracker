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

    public static CardModel? CurrentPlayingCard => _currentPlayingCard.Value;

    public static void StartCardPlay(CardModel card)
    {
        _currentPlayingCard.Value = card;
        _deferredDraws.Value = new List<CardModel>();
    }

    public static void EndCardPlay()
    {
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _currentPlayingCard.Value = null;
    }

    public static bool IsCardPlayActive() => _currentPlayingCard.Value != null;
    
    public static void ClearStateForTarget(Creature target)
    {
        lock (SyncRoot)
        {
            PoisonShares.Remove(target);
            ClearStrangle(target);
        }
    }
    
    public static void DeferDraw(CardModel card)
    {
        _deferredDraws.Value?.Add(card);
    }

    private static void ProcessDeferredDraws()
    {
        if (_deferredDraws.Value == null) return;
        
        foreach (var card in _deferredDraws.Value)
        {
            RegisterCard(card);
            AddDraw(card);
        }
        _deferredDraws.Value.Clear();
    }

    private static ActData? GetActData(EntityStats stat, int actNum)
    {
        return actNum switch
        {
            1 => stat.Act1,
            2 => stat.Act2,
            3 => stat.Act3,
            4 => stat.Act4,
            _ => null
        };
    }
    
    private static readonly string SavePath = ProjectSettings.GlobalizePath("user://deck_tracker_save.json");

    public static event Action<List<CardStats>>? Changed;

    // --- Persistence Logic ---

    public static void SyncRun(string runSeed)
    {
        if (string.IsNullOrEmpty(runSeed)) return;

        lock (SyncRoot)
        {
            if (_currentRunSeed == runSeed) return;

            _currentRunSeed = runSeed;

            if (TryLoadState(runSeed))
            {
                GD.Print($"[DeckTracker] Successfully resumed run data for seed: {runSeed}");
            }
            else
            {
                GD.Print($"[DeckTracker] Starting fresh tracker for new run seed: {runSeed}");
                ResetRun();
            }
            // CLEAN ARCHITECTURE: Re-map the live memory objects immediately after a Load or Reset!
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
                if (string.IsNullOrEmpty(_currentRunSeed)) return;

                state = new SavedRunState
                {
                    RunSeed = _currentRunSeed,
                    Totals = Totals.ToDictionary(kvp => kvp.Key, kvp => (CardStats)kvp.Value.Clone()),
                    // Save the potions!
                    Potions = PotionLedger.ToDictionary(kvp => kvp.Key, kvp => (PotionStats)kvp.Value.Clone())
                };
            }

            string json = JsonSerializer.Serialize(state, SavedStateCtx.Default.SavedRunState);
            System.IO.File.WriteAllText(SavePath, json);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[DeckTracker] Failed to save state: {e.Message}");
        }
    }

    private static bool TryLoadState(string targetSeed)
    {
        try
        {
            if (!System.IO.File.Exists(SavePath)) return false;

            string json = System.IO.File.ReadAllText(SavePath);
            SavedRunState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedRunState);
            
            if (state == null || state.RunSeed != targetSeed) return false;

            Totals = state.Totals;
            PotionLedger = state.Potions ?? new Dictionary<string, PotionStats>();
            return true;
        }
        catch
        {
            return false;
        }
        }

        public static string GetTrackingId(CardModel? card)    {
        if (card == null) return "";
        CardModel sourceCard = card.DeckVersion ?? card;

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";

        return $"{baseId}_F{floorAdded}_U{upgradeLevel}_{enchant}";
    }

    private static void ResetInternalsCombat()
    {
        _currentCombatType = "Unknown"; // Clear the state
        _incrementedThisCombat.Clear(); 
        ForgeHistory.Clear();
        ConquerorTracker.Clear();
        BladeReplayModifierTracker.Clear();
        ResetPoisonState();
        ResetStrangleState();
        ResetOblivionState();
        ResetSerpentFormState();
        ResetReaperFormState();
        ResetBlackHoleState();
        ResetSleightOfFleshState();
        ResetSpeedsterState();
        ResetThunderState();
        ResetStormState();
        ResetHailstormState();
        ResetFanOfKnivesState();
        ResetNecroMasteryState();
        ResetThornsState();
        ResetFlameBarrierState();
        ResetFumesState();
        ResetDoomState();
        ResetCountdownState();
        ResetReflectState();
        ResetRollingBoulderState();
        ClearCorrosiveWaveShares();
        EnvenomShares.Clear();
        ResetOrbState();
        ResetBuffState();
        TrashToTreasureShares.Clear();
        InstancedPowerSources.Clear();
        ResetRitualState();
        DemiseLedgers.Clear();
        IsDemiseExecuting.Value = false;
    }
    
    public static void ClearSession()
    {
        lock (SyncRoot)
        {
            _currentRunSeed = "";
            Godot.GD.Print("[DeckTracker] Session cleared. Ready for next run/continue.");
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
            RelicLedger.Clear(); 
            RelicNameCache.Clear();
            PotionLedger.Clear();
            PotionInstanceIds.Clear();
            _potionCounter = 0;
        }
        Publish();
    }
    
    public static void SyncDeckState(int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            var copyCounts = activeDeckIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            
            HashSet<string> uniqueActiveIds = new HashSet<string>(activeDeckIds);
            
            GD.Print($"[DeckTracker] {activeDeckIds}");
            foreach (var stat in Totals.Values)
            {
                // DIFF CHECK: If we think the card is in the deck, but the game scan didn't find it
                if (stat.IsActive && !uniqueActiveIds.Contains(stat.Id))
                {
                    GD.Print($"[DeckTracker] {stat.Id} is gone");
                    stat.IsActive = false;
                    stat.CopiesInDeck = 0;
                        
                    int floorLeft = Math.Max(1, currentFloor - 1);
                    if (stat.FloorRemoved == -1)
                    {
                        stat.FloorLeftDeck = floorLeft;
                        GD.Print($"[DeckTracker] {stat.Id} FloorLeftDeck updated to {stat.FloorLeftDeck}");
                    }
                }
                else if (copyCounts.TryGetValue(stat.Id, out int count))
                {
                    stat.IsActive = true;
                    stat.CopiesInDeck = count;
                }
            }
        }
        Publish(); // Instantly update the UI, even outside of combat!
    }
    
    public static MegaCrit.Sts2.Core.Runs.RunState? GetLiveRunState()
    {
        // Uses Harmony to securely fetch the private 'State' property from RunManager
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
        if (run == null) return;

        foreach (var player in run.Players)
        {
            // 1. Restore Relic Instances
            foreach (var relic in player.Relics)
            {
                RelicNameCache[relic.Id.Entry] = relic.Title.GetFormattedText();
                var stats = GetOrCreateRelicStats(relic.Id.Entry);
                stats.Model = relic;
                stats.IsActive = true; 
            }

            // 2. Restore Master Deck Card Instances (Fixes the JSON load issue for cards too!)
            foreach (var card in player.Deck.Cards)
            {
                string trackingId = GetTrackingId(card);
                if (Totals.TryGetValue(trackingId, out var cardStats))
                {
                    cardStats.Model = card;
                }
            }
            
            // 3. Restore Potion Instances cleanly
            for (int i = 0; i < player.PotionSlots.Count; i++)
            {
                var potion = player.PotionSlots[i];
                if (potion == null) continue;

                // Find if this live object already has a tracked ID mapped
                string? existingId = PotionInstanceIds.FirstOrDefault(kvp => kvp.Key == potion).Value;

                if (string.IsNullOrEmpty(existingId))
                {
                    // If it's not mapped in memory, check if a ledger entry from JSON matches this potion type and lacks an instance
                    existingId = PotionLedger.Values.FirstOrDefault(p => p.Model == null && p.Id.Contains(potion.Id.Entry))?.Id;
                    
                    if (string.IsNullOrEmpty(existingId))
                    {
                        // Fresh Run Startup: It's a Neow potion!
                        _potionCounter++;
                        existingId = $"POTION_{potion.Id.Entry}_{_potionCounter}";
                        
                        string displayName = potion.Title?.GetFormattedText() ?? potion.Id.Entry ?? "Unknown Potion";
                        PotionLedger[existingId] = new PotionStats
                        {
                            Id = existingId,
                            DisplayName = displayName,
                            FloorObtained = 1, // Guaranteed Floor 1 on clean startups
                            IsActive = true
                        };
                    }
                    
                    // Map the live memory reference to the ID
                    PotionInstanceIds[potion] = existingId;
                }

                // Bind the live instance back to the stats object
                PotionLedger[existingId].Model = potion;
            }
        }
        Godot.GD.Print("[DeckTracker] Restored live object instances to the ledger.");
    }
    
    public static void StartCombat(string combatType, int currentFloor, int currentAct, List<string> activeDeckIds)
    {
        // Diff the deck immediately before processing encounters
        SyncDeckState(currentFloor, activeDeckIds);

        lock (SyncRoot)
        {
            _currentAct = currentAct;
            _currentCombatType = combatType;
            GD.Print($"[DeckTracker] Starting combat state: {_currentCombatType} (Act: {_currentAct})");
            
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0; // Wipe the previous combat's text
                stat.RawForgeCombat = 0;
                stat.ConnectedForgeCombat = 0;
                stat.ReceivedForgeCombat = 0;
                
                if (!stat.IsActive) continue; // Skip cards not in the deck
                
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

                if (!stat.IsActive) continue;

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
        lock (SyncRoot)
        {
            ResetInternalsCombat();
        }
        
        SaveState(); // Lock the victory into the hard drive
        Publish();
    }
    
    public static void HandleRemove(CardModel card, int floorRemoved)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
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
            // card.DeckVersion will be null for the master deck cards, so check FloorAddedToDeck for
            // whether the card is generated
            bool isGenerated = card.FloorAddedToDeck == null;
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                CardModel sourceCard = card.DeckVersion ?? card;
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                stat = new CardStats 
                { 
                    Id = uniqueTrackingId, 
                    DisplayName = displayName,
                    Model = card,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1, 
                    IsActive = !isGenerated, // Normal cards are True, Generated are False
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                
                Totals[uniqueTrackingId] = stat;
            }
            
            if (_currentCombatType != "Unknown" && isGenerated)
            {
                // HashSet.Add returns 'true' ONLY if the item wasn't already in the list.
                // This ensures 10 Shivs generated in one fight only increment the denominator by 1.
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
                if (actData != null) actData.TimesDrawn++;
            }
        }
        Publish(); // Instantly update UI when drawn
    }
    
    public static void AddPlay(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                var actData = GetActData(stat, _currentAct);
                if (actData != null) actData.TimesPlayed++;
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
        if (amount <= 0 || string.IsNullOrEmpty(trackingId)) return;

        // --- NEW: THE ROUTING INTERCEPTOR ---
        // If the ID starts with RELIC_, strip the prefix and send it to the Relic Ledger!
        if (trackingId.StartsWith("RELIC_"))
        {
            // "RELIC_Vajra" becomes "Vajra"
            string relicId = trackingId.Substring(6); 
            AddRelicDamage(relicId, amount);
            return;
        }
        
        // NEW: Intercept Potion Damage!
        if (trackingId.StartsWith("POTION_"))
        {
            lock (SyncRoot)
            {
                if (PotionLedger.TryGetValue(trackingId, out var stat))
                {
                    stat.CombatDamage += amount;
                    stat.RunDamage += amount;
                    GD.Print($"Added {amount} damage to Potion: {trackingId}.");
                    
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
                GD.Print($"Added {amount} damage to {trackingId}.");
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    switch (_currentCombatType)
                    {
                        case "Elite":
                            actData.DamageElite += amount;
                            break;
                        case "Boss":
                            actData.DamageBoss += amount;
                            break;
                        case "Hallway":
                            actData.DamageHallway += amount;
                            break;
                    }
                }
            }
        }
        Publish();
    }
    
    public static void ForcePublish() => Publish();

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