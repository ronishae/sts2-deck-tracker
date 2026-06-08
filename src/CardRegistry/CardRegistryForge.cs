using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<Contribution> ForgeHistory = new();
    private static readonly List<FurnaceContribution> FurnaceContributions = [];

    public static void ResetForgeState()
    {
        lock (SyncRoot)
        {
            ForgeHistory.Clear();
            FurnaceContributions.Clear();
            GD.Print("[DeckTracker] ResetForgeState. Forge state cleared.");
        }
    }

    public static void AddRelicForge(string relicId, decimal rawForge, decimal connectedForge, decimal receivedForge)
    {
        lock (SyncRoot)
        {
            var stats = GetOrCreateRelicStats(relicId);
            stats.RawForgeCombat += rawForge;
            stats.ConnectedForgeCombat += connectedForge;
            stats.ReceivedForgeCombat += receivedForge;
            GD.Print($"[DeckTracker] Added {rawForge} Raw / {connectedForge} Connected Forge to Relic: {relicId}");
        }
        Publish();
    }

    public static void AddForgeById(string trackingId, decimal amount)
    {
        lock (SyncRoot)
        {
            if (!EntityLedger.TryGetValue(trackingId, out var entity)) return;

            entity.RawForgeCombat += amount;

            // Relics don't track act-level forge breakdowns.
            if (entity is not RelicStats)
            {
                entity.GetAct(_currentAct)?.AddRawForge(_currentCombatType, amount);
            }

            ForgeHistory.Add(new Contribution { TrackingId = trackingId, Amount = amount });
        }
        Publish();
    }

    public static void AddForge(CardModel card, decimal amount)
    {
        AddForgeById(GetTrackingId(card), amount);
    }

    public static void UpdateFurnaceHistory(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null) return;
        lock (SyncRoot)
        {
            // Since Furnace power generally doesn't decrease, we only track additions
            // in the exact order they are played.
            if (amount > 0)
            {
                FurnaceContributions.Add(new FurnaceContribution { CardSource = cardSource, PowerAmount = amount });
                GD.Print($"[DeckTracker] Furnace contribution added: {GetTrackingId(cardSource)} for {amount} power.");
            }
        }
        Publish();
    }

    public static void AddPotionForge(PotionModel potion, decimal amount)
    {
        string? potionId;
        lock (SyncRoot)
        {
            if (!PotionInstanceIds.TryGetValue(potion, out potionId))
            {
                GD.Print($"[DeckTracker] AddPotionForge. Potion: {potion.Id.Entry} not found in PotionInstanceIds.");
                return;
            }
            GD.Print($"[DeckTracker] AddPotionForge. Potion: {potion.Id.Entry}, Id: {potionId}, Amount: {amount}");
        }
        AddForgeById(potionId, amount);
    }

    public static void HandleFurnaceForge(decimal forgeAmount)
    {
        if (forgeAmount <= 0) return;

        List<(CardModel card, decimal amount)> attributions = new();

        lock (SyncRoot)
        {
            var remainingForge = forgeAmount;
            foreach (var contribution in FurnaceContributions)
            {
                if (remainingForge <= 0) break;
                var amountToAttribute = Math.Min(remainingForge, contribution.PowerAmount);
                attributions.Add((contribution.CardSource, amountToAttribute));
                remainingForge -= amountToAttribute;
            }

            if (remainingForge > 0)
            {
                GD.Print($"[DeckTracker] Warning: Furnace forge triggered with {remainingForge} unaccounted for by card history.");
            }
        }

        foreach (var attr in attributions)
        {
            GD.Print($"[DeckTracker] Attributing {attr.amount} forge to Furnace source {GetTrackingId(attr.card)}.");
            AddForge(attr.card, attr.amount);
        }
    }
}

public class FurnaceContribution
{
    public CardModel CardSource { get; init; } = null!;
    public decimal PowerAmount { get; init; }
}
