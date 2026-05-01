using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class DeckDamageService
{
    private static readonly object SyncRoot = new();
    
    // Tracks the damage by unique card instance
    private static readonly Dictionary<string, CardStats> Totals = new();
    
    // Keeps track of how many of a specific card type we've seen (for naming purposes)
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
            TypeCounters.Clear(); // Wipe the numbering for the new run
        }
        Publish();
    }

    public static void RecordDamage(CardModel card, decimal damage)
    {
        if (damage <= 0) return;

        string baseId = card.Id.Entry ?? "Unknown_ID";
        string uniqueTrackingId;

        // 1. Try to track it by its permanent Deck Version.
        // The game creates temporary clones in combat, but they should point back to the master deck card.
        if (card.DeckVersion != null)
        {
            uniqueTrackingId = RuntimeHelpers.GetHashCode(card.DeckVersion).ToString();
        }
        // 2. If it has no Deck Version (e.g., generated mid-combat like a Shiv), track the instance itself.
        else
        {
            uniqueTrackingId = RuntimeHelpers.GetHashCode(card).ToString();
        }
        
        lock (SyncRoot)
        {
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                // Assign a number (e.g., "Strike #1") the first time we see this unique card
                if (!TypeCounters.ContainsKey(baseId)) 
                {
                    TypeCounters[baseId] = 0;
                }
                TypeCounters[baseId]++;

                string displayName = $"{card.Title ?? baseId} #{TypeCounters[baseId]}";

                stat = new CardStats 
                { 
                    CardId = uniqueTrackingId, 
                    DisplayName = displayName 
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
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId,
            DisplayName = DisplayName,
            CombatDamage = CombatDamage,
            RunDamage = RunDamage
        };
    }
}