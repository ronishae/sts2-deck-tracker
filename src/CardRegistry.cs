using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class CardRegistry
{
    private static readonly object SyncRoot = new();
    
    // Central Data Store
    private static readonly Dictionary<string, CardStats> Totals = new();
    private static readonly Dictionary<string, int> TypeCounters = new();

    public static event Action<List<CardStats>>? Changed;

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
            TypeCounters.Clear(); 
        }
        Publish();
    }

    // Helper to get the unique memory ID
    private static string GetTrackingId(CardModel card)
    {
        return card.DeckVersion != null 
            ? RuntimeHelpers.GetHashCode(card.DeckVersion).ToString() 
            : RuntimeHelpers.GetHashCode(card).ToString();
    }

    // Central Registration
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
        
        Publish();
    }

    // Central Data Modifier
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