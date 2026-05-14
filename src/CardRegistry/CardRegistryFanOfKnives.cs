using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
   // --- SHIV & FAN OF KNIVES TRACKING ---

    public class ShivDamageRecord
    {
        public CardModel CardModel { get; set; } = null!;
        public decimal PeeledDamage { get; set; }
    }

    private static CardModel? _activeFanOfKnivesCard;
    private static readonly List<ShivDamageRecord> ShivDamageHistory = new();

    public static void ResetFanOfKnivesState()
    {
        lock (SyncRoot)
        {
            _activeFanOfKnivesCard = null;
            ShivDamageHistory.Clear();
        }
    }

    public static void UpdateFanOfKnives(CardModel fanOfKnivesCard)
    {
        lock (SyncRoot)
        {
            _activeFanOfKnivesCard ??= fanOfKnivesCard;
            GD.Print($"[DeckTracker] Updated active Fan of Knives card: {GetTrackingId(_activeFanOfKnivesCard)}.");
        }
    }

    // UPDATED: Now accepts the peeled damage directly!
    public static void AddShivDamage(CardModel cardModel, decimal peeledDamage)
    {
        lock (SyncRoot)
        {
            ShivDamageHistory.Add(new ShivDamageRecord { CardModel = cardModel, PeeledDamage = peeledDamage });
        }
    }

    public static void ProcessShivHistory(CardPlay cardPlay)
    {
        lock (SyncRoot)
        {
            if (ShivDamageHistory.Count == 0) return;

            GD.Print($"[DeckTracker] Processing Shiv history with count: {ShivDamageHistory.Count}");

            // Find the primary target (the one that took the most base damage)
            var maxDamageRecord = ShivDamageHistory[0];
            foreach (var record in ShivDamageHistory)
            {
                if (record.PeeledDamage > maxDamageRecord.PeeledDamage)
                {
                    maxDamageRecord = record;
                }
            }

            foreach (var record in ShivDamageHistory)
            {
                if (record.CardModel == null) continue;

                if (record == maxDamageRecord)
                {
                    // Primary hit belongs to the Shiv
                    AddDamage(record.CardModel, record.PeeledDamage);
                }
                else
                {
                    // Spillover belongs to Fan of Knives
                    if (_activeFanOfKnivesCard != null)
                    {
                        GD.Print($"[DeckTracker] Attributing spillover Shiv damage to Fan of Knives.");
                        AddDamage(_activeFanOfKnivesCard, record.PeeledDamage);
                    }
                    else
                    {
                        AddDamage(record.CardModel, record.PeeledDamage);
                    }
                }
            }

            ShivDamageHistory.Clear();
        }
        Publish();
    }
}
