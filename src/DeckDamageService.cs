using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class DeckDamageService
{
    private static readonly object SyncRoot = new();
    
    private static readonly Dictionary<string, CardStats> Totals = new();
    private static readonly Dictionary<string, int> TypeCounters = new();

    public static event Action<List<CardStats>>? Changed;

    public static void ResetCombat()
    {
        lock (SyncRoot)
        {
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0;
            }
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

    public static void RecordDamage(CardModel card, decimal damage)
    {
        if (damage <= 0) return;

        string baseId = card.Id.Entry ?? "Unknown_ID";
        string uniqueTrackingId;

        if (card.DeckVersion != null)
        {
            uniqueTrackingId = RuntimeHelpers.GetHashCode(card.DeckVersion).ToString();
        }
        else
        {
            uniqueTrackingId = RuntimeHelpers.GetHashCode(card).ToString();
        }
        
        lock (SyncRoot)
        {
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                if (!TypeCounters.ContainsKey(baseId)) 
                {
                    TypeCounters[baseId] = 0;
                }
                TypeCounters[baseId]++;

                string displayName = $"{card.Title ?? baseId} #{TypeCounters[baseId]}";

                // Grab the floor it was added. If it's null, default to 0 (generated card)
                // Starter deck is 1
                int floorAdded = card.DeckVersion != null 
                    ? (card.DeckVersion.FloorAddedToDeck ?? 0) 
                    : (card.FloorAddedToDeck ?? 0);

                stat = new CardStats 
                { 
                    CardId = uniqueTrackingId, 
                    DisplayName = displayName,
                    FloorAdded = floorAdded // Store the floor here!
                };
                Totals[uniqueTrackingId] = stat;
            }

            stat.CombatDamage += damage;
            stat.RunDamage += damage;
        }

        Publish();
    }

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
    public int FloorAdded { get; set; } // <--- New Data Field
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId,
            DisplayName = DisplayName,
            FloorAdded = FloorAdded, // <--- Clone the new field
            CombatDamage = CombatDamage,
            RunDamage = RunDamage
        };
    }
}