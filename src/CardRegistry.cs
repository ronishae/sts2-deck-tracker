using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class CardRegistry
{
    private static readonly object SyncRoot = new();
    
    private static Dictionary<string, CardStats> Totals = new();
    
    private static string _currentRunSeed = "";

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
        // Always try to use the Master Deck version for the most accurate persistent data
        CardModel sourceCard = card.DeckVersion ?? card;

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";

        // Creates an unbreakable fingerprint: e.g., "BASH_IRONCLAD_F0_U0_None"
        return $"{baseId}_F{floorAdded}_U{upgradeLevel}_{enchant}";
    }

    // ------------------------------

    public static void ResetCombat()
    {
        lock (SyncRoot)
        {
            foreach (var stat in Totals.Values) stat.CombatDamage = 0;
        }
        Publish();
    }

    public static void ResetRun()
    {
        lock (SyncRoot)
        {
            Totals.Clear();
        }
        Publish();
    }

    public static void RegisterCard(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                CardModel sourceCard = card.DeckVersion ?? card;
                
                // If it is upgraded, append the '+' so it looks clean in the UI
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
            }
        }

        Publish();
    }
    
    public static void ProcessCombatEnd(string combatType)
    {
        lock (SyncRoot)
        {
            // We iterate through every card currently in the registry
            foreach (var stat in Totals.Values)
            {
                // Increment the "Seen" counters so we can calculate accurate averages
                stat.EncountersSeenTotal++;
                
                if (combatType == "Elite") stat.EncountersSeenElite++;
                else if (combatType == "Boss") stat.EncountersSeenBoss++;
                else stat.EncountersSeenHallway++;

                // If the card dealt damage this combat, lock it into the specific category
                if (stat.CombatDamage > 0)
                {
                    if (combatType == "Elite") stat.DamageElite += stat.CombatDamage;
                    else if (combatType == "Boss") stat.DamageBoss += stat.CombatDamage;
                    else stat.DamageHallway += stat.CombatDamage;
                }
                
            }
        }
        
        SaveState();
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
    
    // Volatile (Resets every combat)
    public decimal CombatDamage { get; set; }
    
    // Persistent Totals
    public decimal RunDamage { get; set; }
    public decimal DamageHallway { get; set; }
    public decimal DamageElite { get; set; }
    public decimal DamageBoss { get; set; }

    // Persistent Encounter Counters
    public int EncountersSeenTotal { get; set; }
    public int EncountersSeenHallway { get; set; }
    public int EncountersSeenElite { get; set; }
    public int EncountersSeenBoss { get; set; }

    // Helpers for calculating averages safely
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