using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class DeckDamageService
{
    private static readonly object SyncRoot = new();
    private static readonly Dictionary<string, CardStats> Totals = new();

    public static event Action<List<CardStats>>? Changed;

    public static void ResetCombat()
    {
        lock (SyncRoot)
        {
            // Only zero out combat damage, keep the run damage intact!
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
            // Completely wipe the slate for a new run
            Totals.Clear();
        }
        Publish();
    }

    public static void RecordDamage(CardModel card, decimal damage)
    {
        if (damage <= 0) return;

        string cardId = card.Id.Entry ?? "Unknown_ID";
        
        lock (SyncRoot)
        {
            if (!Totals.TryGetValue(cardId, out CardStats? stat))
            {
                stat = new CardStats 
                { 
                    CardId = cardId, 
                    DisplayName = card.Title?.ToString() ?? cardId 
                };
                Totals[cardId] = stat;
            }

            // Increment both!
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