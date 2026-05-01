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

            stat.TotalDamage += damage;
        }

        Publish();
    }

    private static void Publish()
    {
        List<CardStats> sortedList;
        lock (SyncRoot)
        {
            sortedList = Totals.Values
                .OrderByDescending(s => s.TotalDamage)
                .Select(s => s.Clone())
                .ToList();
        }

        Changed?.Invoke(sortedList);
    }
}

public sealed class CardStats
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public decimal TotalDamage { get; set; }

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId,
            DisplayName = DisplayName,
            TotalDamage = TotalDamage
        };
    }
}