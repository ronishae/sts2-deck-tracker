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
    private static Dictionary<string, int> TypeCounters = new();
    
    private static string _currentRunSeed = "";

    // Godot's safe user data path. This translates to the STS2 AppData folder on Windows/Mac!
    private static readonly string SavePath = ProjectSettings.GlobalizePath("user://deck_tracker_save.json");

    public static event Action<List<CardStats>>? Changed;

    // --- Persistence Logic ---

    public static void SyncRun(string runSeed)
    {
        if (string.IsNullOrEmpty(runSeed)) return;

        lock (SyncRoot)
        {
            // If we are already tracking this run in memory, do nothing
            if (_currentRunSeed == runSeed) return;

            _currentRunSeed = runSeed;

            // Try to load from disk
            if (TryLoadState(runSeed))
            {
                GD.Print($"[DeckTracker] Successfully resumed run data for seed: {runSeed}");
            }
            else
            {
                // New run, or no save file existed
                GD.Print($"[DeckTracker] Starting fresh tracker for new run seed: {runSeed}");
                Totals.Clear();
                TypeCounters.Clear();
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
                    Totals = Totals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone()),
                    TypeCounters = new Dictionary<string, int>(TypeCounters)
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
            TypeCounters = state.TypeCounters;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------

    public static void ResetCombat()
    {
        lock (SyncRoot)
        {
            foreach (var stat in Totals.Values) stat.CombatDamage = 0;
        }
        Publish();
    }

    private static string GetTrackingId(CardModel card)
    {
        return card.DeckVersion != null 
            ? RuntimeHelpers.GetHashCode(card.DeckVersion).ToString() 
            : RuntimeHelpers.GetHashCode(card).ToString();
    }

    public static void RegisterCard(CardModel card)
    {
        string baseId = card.Id.Entry ?? "Unknown_ID";
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                if (!TypeCounters.ContainsKey(baseId)) TypeCounters[baseId] = 0;
                TypeCounters[baseId]++;

                string displayName = $"{card.Title ?? baseId} #{TypeCounters[baseId]}";
                int floorAdded = card.DeckVersion != null 
                    ? (card.DeckVersion.FloorAddedToDeck ?? 0) 
                    : (card.FloorAddedToDeck ?? 0);

                stat = new CardStats 
                { 
                    CardId = uniqueTrackingId, 
                    DisplayName = displayName,
                    FloorAdded = floorAdded,
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
    public int FloorAdded { get; set; } 
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId, DisplayName = DisplayName, FloorAdded = FloorAdded, 
            CombatDamage = CombatDamage, RunDamage = RunDamage
        };
    }
}

// --- JSON Serialization Models ---
public sealed class SavedRunState
{
    public string RunSeed { get; set; } = "";
    public Dictionary<string, CardStats> Totals { get; set; } = new();
    public Dictionary<string, int> TypeCounters { get; set; } = new();
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedRunState))]
internal partial class SavedStateCtx : System.Text.Json.Serialization.JsonSerializerContext { }