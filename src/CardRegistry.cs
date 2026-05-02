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

    private static string GetTrackingId(CardModel card)
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
    public static void StartCombat(string combatType)
    {
        lock (SyncRoot)
        {
            _currentCombatType = combatType;

            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0; // Wipe the previous combat's text
                
                // Increment the "seen" counters right at the start of the fight
                stat.EncountersSeenTotal++;
                
                if (combatType == "Elite") stat.EncountersSeenElite++;
                else if (combatType == "Boss") stat.EncountersSeenBoss++;
                else stat.EncountersSeenHallway++;
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

    public static void RegisterCard(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
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
                    CombatDamage = 0,
                    RunDamage = 0
                };

                // CRITICAL: If a completely new card is added mid-combat (like a Shiv), 
                // it needs to count THIS combat as its first encounter so its average doesn't break!
                if (_currentCombatType != "Unknown")
                {
                    stat.EncountersSeenTotal = 1;
                    if (_currentCombatType == "Elite") stat.EncountersSeenElite = 1;
                    else if (_currentCombatType == "Boss") stat.EncountersSeenBoss = 1;
                    else stat.EncountersSeenHallway = 1;
                }

                Totals[uniqueTrackingId] = stat;
            }
        }
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