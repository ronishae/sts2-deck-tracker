using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static CardModel? _activeFanOfKnivesCard;
    private static readonly List<DamageHistoryItem> ShivDamageHistory = new();

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

    public static void AddShivDamageHistoryItem(DamageHistoryItem damageHistoryItem)
    {
        lock (SyncRoot)
        {
            ShivDamageHistory.Add(damageHistoryItem);
        }
    }

    public static void ProcessShivHistory(CardPlay cardPlay)
    {
        lock (SyncRoot)
        {
            if (ShivDamageHistory.Count == 0) return;

            GD.Print($"[DeckTracker] Processing Shiv history with count: {ShivDamageHistory.Count}");

            var maxTotalDamageInstance = ShivDamageHistory[0];
            foreach (var damageHistoryItem in ShivDamageHistory)
            {
                if (damageHistoryItem.Results.TotalDamage > maxTotalDamageInstance.Results.TotalDamage)
                {
                    maxTotalDamageInstance = damageHistoryItem;
                }
            }

            foreach (var damageHistoryItem in ShivDamageHistory)
            {
                if (damageHistoryItem.CardModel == null) continue;

                if (damageHistoryItem == maxTotalDamageInstance)
                {
                    AddDamage(damageHistoryItem.CardModel, damageHistoryItem.Results.TotalDamage);
                }
                else
                {
                    if (_activeFanOfKnivesCard != null)
                    {
                        GD.Print($"[DeckTracker] Attributing spillover Shiv damage to Fan of Knives.");
                        AddDamage(_activeFanOfKnivesCard, damageHistoryItem.Results.TotalDamage);
                    }
                    else
                    {
                        // Should not happen unless AoE Shivs exist without Fan of Knives, but just in case
                        AddDamage(damageHistoryItem.CardModel, damageHistoryItem.Results.TotalDamage);
                    }
                }
            }

            ShivDamageHistory.Clear();
        }
        Publish();
    }
}
