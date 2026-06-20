using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static void ClearStateForTarget(Creature target)
    {
        lock (SyncRoot)
        {
            Log.Debug($"ClearStateForTarget. Target: {target.Name}");
            PoisonShares.Remove(target);
            foreach (var tracker in TargetedTrackers.Values)
            {
                tracker.ClearTarget(target);
            }
        }
    }

    public static void HandleRemove(CardModel card, int floorRemoved)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity) && entity is CardStats stat)
            {
                Log.Debug($"HandleRemove. Card: {uniqueTrackingId}");
                if (stat.CopiesInDeck > 1)
                {
                    stat.CopiesInDeck--;
                }
                else
                {
                    stat.FloorRemoved = floorRemoved;
                    stat.FloorLeftDeck = floorRemoved;
                    stat.IsActive = false;
                    stat.CopiesInDeck = 0;
                    // Remember the object under its removal id so we can revive this entry if the SAME card
                    // returns to the deck under a new id (e.g. Thieving Hopper rewrites FloorAddedToDeck).
                    _removedDeckCardIds[ResolveSourceCard(card)] = uniqueTrackingId;
                    Log.Debug($"HandleRemove. Recorded for revival. Card: {uniqueTrackingId}");
                }
            }
        }
        Publish();
    }

    public static void RegisterCard(CardModel card, string? ownerNetId = null, bool isDeckScan = false)
    {
        CardModel sourceCard = ResolveSourceCard(card);
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);

        lock (SyncRoot)
        {
            // Assign the copy index before building the id: deterministic ordinal for deck scans,
            // persistent counter for generated/combat cards not seen in the current scan.
            if (isDeckScan)
            {
                AssignDeckCopyIndex(sourceCard, owner);
            }
            else if (!_cardInstanceIds.ContainsKey(sourceCard))
            {
                GetOrAssignCopyIndex(sourceCard);
            }
        }

        string uniqueTrackingId = GetTrackingId(card, owner);

        lock (SyncRoot)
        {
            bool isGenerated = card.FloorAddedToDeck == null;
            if (isGenerated && !IsStatusCard(card) && !IsGenerationAttributionExcluded(card)
                && !EntityLedger.ContainsKey(uniqueTrackingId))
            {
                TryTagGeneratedCard(card);
            }
            // Deck scans recompute ids deterministically and rely on the existing version-merge UI, so only
            // live draw/play registrations migrate a card whose identity changed mid-combat.
            if (!isDeckScan)
            {
                MigrateStatsOnIdentityChange(card, uniqueTrackingId);
            }
            else
            {
                TryReviveReturnedDeckCard(sourceCard, uniqueTrackingId);
            }
            if (!EntityLedger.TryGetValue(uniqueTrackingId, out var existing) || existing is not CardStats stat)
            {
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                Log.Debug($"RegisterCard. NEW Card: {uniqueTrackingId}, Generated: {isGenerated}");
                stat = new CardStats
                {
                    Id = uniqueTrackingId,
                    DisplayName = displayName,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    UpgradeLevel = sourceCard.CurrentUpgradeLevel,
                    BaseCardKey = GetBaseCardKey(card, owner),
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1,
                    IsActive = !isGenerated,
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                EntityLedger[uniqueTrackingId] = stat;
            }

            if (sourceCard.Id.Entry == "SOVEREIGN_BLADE")
                RegisterBladeForgeHistory(sourceCard);

            stat.PlayerIndex = ResolvePlayerIndex(owner);
            stat.Model = card;
            if (_cardGeneratedBy.TryGetValue(card, out var generatorId))
            {
                stat.GeneratedById = generatorId;
            }
            if (_cardGeneratedByImmediate.TryGetValue(card, out var immediateGeneratorId))
            {
                stat.GeneratedByImmediateId = immediateGeneratorId;
            }

            if (_currentCombatType != "Unknown" && isGenerated && _incrementedThisCombat.Add(uniqueTrackingId))
            {
                stat.GetAct(_currentAct)?.AddEncounterSeen(_currentCombatType);
            }
        }
    }

    public static void AddDraw(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity))
            {
                var actData = entity.GetAct(_currentAct);
                if (actData != null) actData.TimesDrawn++;
                entity.CombatTimesDrawn++;
                Log.Debug($"AddDraw. Card: {uniqueTrackingId}");
            }
        }
        Publish();
    }

    // Registers a card and credits a draw in one step, used when a draw must be counted outside the
    // normal draw hooks (e.g. a card autoplayed without ever entering the hand). Done directly rather
    // than via the deferral window so the draw pairs with the play, which is also counted directly.
    public static void RegisterAndAddDraw(CardModel card)
    {
        RegisterCard(card);
        AddDraw(card);
        ForcePublish();
    }

    public static void AddPlay(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(uniqueTrackingId, out var entity))
            {
                var actData = entity.GetAct(_currentAct);
                if (actData != null) actData.TimesPlayed++;
                entity.CombatTimesPlayed++;
                Log.Debug($"AddPlay. Card: {uniqueTrackingId}");
            }
        }
        Publish();
    }

    public static void AddDamage(CardModel card, decimal amount)
    {
        // Generated-damage routing happens inside AddDamageById (keyed off the ledger entry's GeneratedById),
        // so every damage path — direct, poison, orbs, ... — credits the generator's bucket exactly once.
        AddDamageById(GetTrackingId(card), amount);
    }

    // If this card was generated by a known source, also credit the generator's separate generated-damage
    // bucket so the overlay can attribute the generated card's damage back to its creator. The generated
    // card still keeps its own direct-damage row; this is an additive second bucket, never a replacement.
    // Caller must hold SyncRoot. Works for ANY damage path because it reads the ledger entry's GeneratedById
    // (set at registration to the root generator) rather than the CardModel-keyed map.
    private static void RouteGeneratedDamage(string generatedTrackingId, decimal amount)
    {
        if (amount <= 0
            || !EntityLedger.TryGetValue(generatedTrackingId, out var entity)
            || entity is not CardStats stat
            || string.IsNullOrEmpty(stat.GeneratedById)
            || stat.GeneratedById == generatedTrackingId)
        {
            return;
        }
        if (!EntityLedger.TryGetValue(stat.GeneratedById, out var generator))
        {
            Log.Warn($"RouteGeneratedDamage. Generator {stat.GeneratedById} for {generatedTrackingId} not in ledger; generated damage {amount} dropped.");
            return;
        }
        generator.AddGeneratedDamage(amount, _currentAct, _currentCombatType);
        Log.VeryDebug($"RouteGeneratedDamage. Id: {generatedTrackingId}, Amount: {amount}, Generator: {stat.GeneratedById}");
    }

    public static void AddDamageById(string trackingId, decimal amount)
    {
        if (amount <= 0 || string.IsNullOrEmpty(trackingId))
        {
            return;
        }

        lock (SyncRoot)
        {
            if (EntityLedger.TryGetValue(trackingId, out var entity))
            {
                entity.AddCombatDamage(amount, _currentAct, _currentCombatType);
                Log.Debug($"AddDamageById. Amount: {amount}, ID: {trackingId}");
                RouteGeneratedDamage(trackingId, amount);
            }
        }
        Publish();
    }
}
