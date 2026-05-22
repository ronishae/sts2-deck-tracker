using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
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

    private static ActData? GetActData(CardStats stat, int actNum)
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
                    Totals = Totals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone())
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
    
    public static void ResetRun()
    {
        lock (SyncRoot)
        {
            Totals.Clear();
            ForgeHistory.Clear();
            _incrementedThisCombat.Clear();
            _currentAct = 1;
            _currentCombatType = "Unknown";
            
            // Note: Combat-specific states (Poison, Orbs, etc) are already 
            // reset at StartCombat, so we don't need to duplicate that here.
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
                if (stat.IsInDeck && !uniqueActiveIds.Contains(stat.CardId))
                {
                    GD.Print($"[DeckTracker] {stat.CardId} is gone");
                    stat.IsInDeck = false;
                    stat.CopiesInDeck = 0;
                        
                    int floorLeft = Math.Max(1, currentFloor - 1);
                    if (stat.FloorRemoved == -1)
                    {
                        stat.FloorLeftDeck = floorLeft;
                        GD.Print($"[DeckTracker] {stat.CardId} FloorLeftDeck updated to {stat.FloorLeftDeck}");
                    }
                }
                else if (copyCounts.TryGetValue(stat.CardId, out int count))
                {
                    stat.IsInDeck = true;
                    stat.CopiesInDeck = count;
                }
            }
        }
        Publish(); // Instantly update the UI, even outside of combat!
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
            ResetOrbState();
            ResetBuffState();
            
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0; // Wipe the previous combat's text
                stat.RawForgeCombat = 0;
                stat.ConnectedForgeCombat = 0;
                stat.ReceivedForgeCombat = 0;
                
                if (!stat.IsInDeck) continue; // Skip cards not in the deck
                
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    if (combatType == "Elite") actData.EncountersSeenElite++;
                    else if (combatType == "Boss") actData.EncountersSeenBoss++;
                    else actData.EncountersSeenHallway++;
                }

                _incrementedThisCombat.Add(stat.CardId);
            }
        }
    }
    
    public static void ProcessCombatEnd()
    {
        lock (SyncRoot)
        {
            _currentCombatType = "Unknown"; // Clear the state
            ForgeHistory.Clear();
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
                    stat.IsInDeck = false;
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
                CardId = uniqueTrackingId, 
                DisplayName = displayName,
                CardType = sourceCard.Type.ToString(),
                Enchantment = enchantName,
                FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                FloorRemoved = isGenerated ? 0 : -1, 
                IsInDeck = !isGenerated, // Normal cards are True, Generated are False
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
            statsCopy = Totals.Values.Select(s => s.Clone()).ToList();
        }
        Changed?.Invoke(statsCopy);
    }
}