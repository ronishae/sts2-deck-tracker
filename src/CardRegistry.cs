using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class CardRegistry
{
    private static readonly object SyncRoot = new();
    
    private static Dictionary<string, CardStats> Totals = new();
    private static string _currentRunSeed = "";
    
    // NEW: We need to know what fight we are in while dealing damage!
    private static string _currentCombatType = "Unknown";

    // Tracks which cards have already received their +1 encounter this specific combat
    private static HashSet<string> _incrementedThisCombat = new();
    
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
                Totals.Clear();
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

    // --- Fingerprint Generation ---

    public static string GetTrackingId(CardModel card)
    {
        CardModel sourceCard = card.DeckVersion ?? card;

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";

        return $"{baseId}_F{floorAdded}_U{upgradeLevel}_{enchant}";
    }

    // --- Combat Lifecycle ---

    public static void ResetRun()
    {
        lock (SyncRoot)
        {
            Totals.Clear();
        }
        Publish();
    }

    // REPLACES ResetCombat: Called before the deck is scanned for a new fight
    public static void StartCombat(string combatType, int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            _currentCombatType = combatType;
            _incrementedThisCombat.Clear();
        
            // Put the newly scanned active IDs into a fast lookup
            HashSet<string> uniqueActiveIds = new HashSet<string>(activeDeckIds);
            
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0; // Wipe the previous combat's text
                
                // DIFF CHECK: If we think the card is in the deck, but the game scan didn't find it
                // it means it was removed, transformed, or upgraded!
                if (stat.IsInDeck && !uniqueActiveIds.Contains(stat.CardId))
                {
                    stat.IsInDeck = false;
                    // FloorRemoved is remove specifically, FloorLeft can be from upgrade, transform, remove
                    if (stat.FloorRemoved == -1) stat.FloorLeftDeck = currentFloor;
                }
                
                // If the card is no longer in the deck (or was generated), skip incrementing encounters
                if (!stat.IsInDeck) continue;
                
                // Increment the "seen" counters right at the start of the fight
                stat.EncountersSeenTotal++;
                
                if (combatType == "Elite") stat.EncountersSeenElite++;
                else if (combatType == "Boss") stat.EncountersSeenBoss++;
                else stat.EncountersSeenHallway++;
                _incrementedThisCombat.Add(stat.CardId);
            }
        }
    }

    public static void ProcessCombatEnd()
    {
        lock (SyncRoot)
        {
            _currentCombatType = "Unknown"; // Clear the state
        }
        
        SaveState(); // Lock the victory into the hard drive
        Publish();
    }

    // --- Data Modifiers ---
    
    public static void HandleRemove(CardModel card, int floorRemoved)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.FloorRemoved = floorRemoved;
                stat.FloorLeftDeck = floorRemoved;
                stat.IsInDeck = false;
            }
        }
        Publish();
    }
    
    public static void RegisterCard(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
            bool isGenerated = card.DeckVersion == null;
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                CardModel sourceCard = card.DeckVersion ?? card;
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                
                stat = new CardStats 
                { 
                    CardId = uniqueTrackingId, 
                    DisplayName = displayName,
                    CardType = sourceCard.Type.ToString(),
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    // NEW: If it has no DeckVersion, it's a generated card, so default it to removed (0).
                    FloorRemoved = isGenerated ? 0 : -1, 
                    IsInDeck = !isGenerated, // Normal cards are True, Generated are False
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
                    stat.EncountersSeenTotal++;
                    if (_currentCombatType == "Elite") stat.EncountersSeenElite++;
                    else if (_currentCombatType == "Boss") stat.EncountersSeenBoss++;
                    else stat.EncountersSeenHallway++;
                }
            }
        }
    }
    
    // NEW: Draw Incrementer
    public static void AddDraw(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.TimesDrawn++;
            }
        }
        Publish(); // Instantly update UI when drawn
    }

    // NEW: Play Incrementer
    public static void AddPlay(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.TimesPlayed++;
            }
        }
        Publish(); // Instantly update UI when played
    }
    
    public static void AddDamage(CardModel card, decimal damage)
    {
        string uniqueTrackingId = GetTrackingId(card);
        
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.CombatDamage += damage;
                stat.RunDamage += damage;

                // NEW: Route the damage in real-time!
                if (_currentCombatType == "Elite") stat.DamageElite += damage;
                else if (_currentCombatType == "Boss") stat.DamageBoss += damage;
                else if (_currentCombatType == "Hallway") stat.DamageHallway += damage;
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

public sealed class CardStats
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CardType { get; set; } = "";
    public int FloorAdded { get; set; }
    public int FloorRemoved { get; set; } = -1;
    public int FloorLeftDeck { get; set; } = -1;
    public bool IsInDeck { get; set; } = true;
    
    public int TimesDrawn { get; set; }
    public int TimesPlayed { get; set; }
    public decimal PlayRate => TimesDrawn > 0 ? (decimal)TimesPlayed / TimesDrawn : 0;
    
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }
    public decimal DamageHallway { get; set; }
    public decimal DamageElite { get; set; }
    public decimal DamageBoss { get; set; }

    public int EncountersSeenTotal { get; set; }
    public int EncountersSeenHallway { get; set; }
    public int EncountersSeenElite { get; set; }
    public int EncountersSeenBoss { get; set; }

    public decimal AvgTotal => EncountersSeenTotal > 0 ? RunDamage / EncountersSeenTotal : 0;
    public decimal AvgHallway => EncountersSeenHallway > 0 ? DamageHallway / EncountersSeenHallway : 0;
    public decimal AvgElite => EncountersSeenElite > 0 ? DamageElite / EncountersSeenElite : 0;
    public decimal AvgBoss => EncountersSeenBoss > 0 ? DamageBoss / EncountersSeenBoss : 0;

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId, DisplayName = DisplayName, CardType = CardType, FloorAdded = FloorAdded, 
            FloorRemoved = FloorRemoved, FloorLeftDeck = FloorLeftDeck, IsInDeck = IsInDeck,
            TimesDrawn = TimesDrawn, TimesPlayed = TimesPlayed, // Add clones here!
            CombatDamage = CombatDamage, RunDamage = RunDamage,
            DamageHallway = DamageHallway, DamageElite = DamageElite, DamageBoss = DamageBoss,
            EncountersSeenTotal = EncountersSeenTotal, EncountersSeenHallway = EncountersSeenHallway,
            EncountersSeenElite = EncountersSeenElite, EncountersSeenBoss = EncountersSeenBoss
        };
    }
}

// --- JSON Serialization Models ---
public sealed class SavedRunState
{
    public string RunSeed { get; set; } = "";
    public Dictionary<string, CardStats> Totals { get; set; } = new();
    
    // We leave this empty dictionary here so older save files don't crash when deserializing!
    public Dictionary<string, int> TypeCounters { get; set; } = new(); 
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedRunState))]
internal partial class SavedStateCtx : System.Text.Json.Serialization.JsonSerializerContext { }