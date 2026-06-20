using Godot;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Maps each physical Sovereign Blade CardModel (resolved via ResolveSourceCard) to the ordered list
    // of forge contributions it has accumulated since it was registered this combat. Keyed by CardModel
    // rather than tracking ID because all blades share one tracking ID (singleton), but each physical
    // instance starts accumulating from a different point in the fight.
    private static readonly Dictionary<CardModel, List<Contribution>> BladeForgeHistories = new();
    private static readonly List<FurnaceContribution> FurnaceContributions = [];

    public static void RegisterBladeForgeHistory(CardModel sourceCardModel)
    {
        if (!BladeForgeHistories.ContainsKey(sourceCardModel))
        {
            BladeForgeHistories[sourceCardModel] = [];
            Log.Debug($"RegisterBladeForgeHistory. Registered forge history for: {GetTrackingId(sourceCardModel)}");
        }
    }

    public static void ResetForgeState()
    {
        lock (SyncRoot)
        {
            BladeForgeHistories.Clear();
            FurnaceContributions.Clear();
            Log.Debug("ResetForgeState. Forge state cleared.");
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
            Log.Debug($"Added {rawForge} Raw / {connectedForge} Connected Forge to Relic: {relicId}");
        }
        Publish();
    }

    public static void AddForgeById(string trackingId, decimal amount)
    {
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(trackingId, out var entity))
            {
                entity.RawForgeCombat += amount;
                entity.GetAct(_currentAct)?.AddRawForge(_currentCombatType, amount);
            }
            else
            {
                Log.Warn($"AddForgeById. Source {trackingId} not in EntityLedger; forge will be attributed to blade without a ConnectedForge source.");
            }

            // Record the contribution in every active blade's per-blade history regardless of whether
            // the forger entity is tracked. DistributeForgeHistory handles a missing entity gracefully
            // (continues past it, still credits the blade's ReceivedForgeCombat).
            var contribution = new Contribution { TrackingId = trackingId, Amount = amount };
            foreach (var history in BladeForgeHistories.Values)
            {
                history.Add(contribution);
            }
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
                Log.Debug($"Furnace contribution added: {GetTrackingId(cardSource)} for {amount} power.");
            }
        }
        Publish();
    }

    public static void AddPotionForge(PotionModel potion, decimal amount)
    {
        // Forge fires during a potion's use, so prefer the id locked in at use-time; otherwise resolve
        // from the model (owner + type), which works for remote players whose model is a network clone.
        var potionId = CurrentPlayingPotionId;
        if (string.IsNullOrEmpty(potionId) && !TryResolvePotionId(potion, out potionId))
        {
            Log.Warn($"AddPotionForge. Potion: {potion.Id.Entry} could not be resolved.");
            return;
        }
        Log.Debug($"AddPotionForge. Potion: {potion.Id.Entry}, Id: {potionId}, Amount: {amount}");
        AddForgeById(potionId, amount);
    }

    public static void HandleHammerTimeForge(HammerTimePower power, decimal amount)
    {
        var sourceId = InstancedTracker.GetIdForInstance(power);
        if (string.IsNullOrEmpty(sourceId))
        {
            Log.Warn($"HandleHammerTimeForge. No source mapped for HammerTimePower ({power.GetHashCode()}).");
            return;
        }
        Log.Debug($"HandleHammerTimeForge. Source: {sourceId}, Amount: {amount}");
        AddForgeById(sourceId, amount);
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
                Log.Warn($"HandleFurnaceForge. Furnace forge triggered with {remainingForge} unaccounted for by card history.");
            }
        }

        foreach (var attr in attributions)
        {
            Log.Debug($"Attributing {attr.amount} forge to Furnace source {GetTrackingId(attr.card)}.");
            AddForge(attr.card, attr.amount);
        }
    }
}
