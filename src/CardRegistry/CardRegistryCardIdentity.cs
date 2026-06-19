using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Resolves the stable per-player owner key for a card: the owning player's NetId (Steam id) as a string.
    // NetId is globally unique and permanent, so a card's identity survives save/load and rejoins regardless
    // of join order. Must be deterministic and never throw — resolved from the same sourceCard used to build
    // the rest of the ID so deck scans and combat always agree. Returns UnknownOwnerKey for unowned cards.
    private static string ResolveOwnerNetId(CardModel? sourceCard)
    {
        try
        {
            var player = sourceCard?.Owner;
            if (player != null)
            {
                return player.NetId.ToString();
            }
        }
        catch (Exception e)
        {
            Log.Error($"ResolveOwnerNetId failed: {e.Message}");
        }
        return UnknownOwnerKey;
    }

    // Cosmetic only: maps a resolved owner NetId to its current ordered player index for row colour/ordering.
    private static int ResolvePlayerIndex(string ownerNetId)
    {
        return _playerIndexByNetId.TryGetValue(ownerNetId, out var idx) ? idx : 0;
    }

    // Clears the per-scan copy-index state. Called once at the start of a full deck scan so deck copy indices
    // are recomputed deterministically (and stale CardModel references from a prior room are dropped).
    public static void BeginDeckScan()
    {
        lock (SyncRoot)
        {
            _cardInstanceIds.Clear();
            _deckScanOrdinals.Clear();
        }
    }

    private static int GetCopyIndex(CardModel sourceCard)
    {
        if (SingletonCardIds.Contains(sourceCard.Id.Entry ?? "Unknown"))
        {
            return 0;
        }
        return _cardInstanceIds.TryGetValue(sourceCard, out var idx) ? idx : 0;
    }

    // Resolves the deck card a transient copy should be attributed to, caching the result so we never
    // re-walk. CreateClone() nulls a clone's DeckVersion but records CloneOf, so we auto-fill the shared
    // _cardCreatedBy map from it; a card-typed creator resolves to ITS origin (one cached lookup). A card
    // with no creator maps to itself, so purely generated cards (SHIV, etc.) keep their current behaviour
    // until the Shiv follow-up sets _cardCreatedBy to a non-card source. Locks internally (SyncRoot is
    // reentrant) since callers like AddDamage invoke this outside their ledger lock.
    private static CardModel ResolveSourceCard(CardModel card)
    {
        lock (SyncRoot)
        {
            if (_cardOrigin.TryGetValue(card, out var cached))
            {
                return cached;
            }
            // Auto-fill the shared creator seam from the game's clone link (Anger).
            if (!_cardCreatedBy.ContainsKey(card) && card.CloneOf != null)
            {
                _cardCreatedBy[card] = card.CloneOf;
            }
            CardModel origin;
            if (_cardCreatedBy.TryGetValue(card, out var creator) && creator is CardModel creatorCard)
            {
                origin = ResolveSourceCard(creatorCard); // creator is virtually always already cached
                Log.VeryDebug($"ResolveSourceCard. Card: {card.Id.Entry} rolled up to origin: {origin.Id.Entry}");
            }
            else
            {
                origin = card.DeckVersion ?? card;
            }
            _cardOrigin[card] = origin;
            return origin;
        }
    }

    // Eagerly captures the generator link the moment a generated card is created into ANY pile, while the
    // generating source is still executing. Cards that overflow a full hand are created straight into the
    // discard pile and never enter Hand here, so they would otherwise only be seen — with no executing
    // context — when later drawn. No-op for non-generated cards, status cards (enemy-added Wound/Dazed/...),
    // and cards already tracked/tagged. Stays silent when no source is executing: RegisterCard warns later.
    public static void TagGeneratedCardOnCreation(CardModel card)
    {
        if (card.FloorAddedToDeck != null || IsStatusCard(card) || IsGenerationAttributionExcluded(card))
        {
            return;
        }
        lock (SyncRoot)
        {
            if (_cardGeneratedBy.ContainsKey(card) || _cardInstanceIds.ContainsKey(card))
            {
                return;
            }
            // If a ledger entry already exists for this card's tracking ID, it's a pre-existing
            // card being repositioned (e.g. Hologram drawing from deck) — not newly generated.
            var trackingId = GetTrackingId(card);
            if (!string.IsNullOrEmpty(trackingId) && EntityLedger.ContainsKey(trackingId))
            {
                return;
            }
            TryApplyGeneratorTag(card);
        }
    }

    // Tags a generated card to whatever source is currently executing. Called at registration (warns when
    // nothing is executing — a likely sign a relic/power generator needs wrapping) and eagerly at creation
    // (silent). Must be called under SyncRoot.
    private static void TryTagGeneratedCard(CardModel card)
    {
        if (TryApplyGeneratorTag(card) || _cardGeneratedBy.ContainsKey(card))
        {
            return;
        }
        Log.Warn($"TryTagGeneratedCard. Card: {card.Id.Entry} generated with no tracked source; falling back to GEN (a relic/power generator may need wrapping).");
    }

    // Attributes a generated card to the source currently executing, in priority order: the card being
    // played -> executing relic -> playing potion -> executing power. The card branch is first and uses the
    // innermost play, so a force-played generator (Mayhem/Cascade auto-playing a Shiv-maker) gets credit
    // over the card/power that played it. Tags the chain root so a generated card that itself generates
    // rolls all the way up to the original creator. Returns true when a tag was applied (or already
    // present); false when no source is executing. Must be called under SyncRoot.
    private static bool TryApplyGeneratorTag(CardModel card)
    {
        if (_cardGeneratedBy.ContainsKey(card))
        {
            return true;
        }

        // A card playing itself is never a generation event (it is being played, not created).
        if (CurrentPlayingCard == card)
        {
            return false;
        }

        string? generatorId;
        if (!string.IsNullOrEmpty(InstancedTracker.ExecutingSourceId))
        {
            // An explicitly-wrapped power generator takes priority even while a card is playing, so
            // reactive generators like CalamityPower (AfterCardPlayed) are credited over the trigger card.
            generatorId = InstancedTracker.ExecutingSourceId;
        }
        else if (CurrentPlayingCard != null)
        {
            var proposedId = GetTrackingId(CurrentPlayingCard);
            if (proposedId == GetTrackingId(card))
            {
                // Same logical card by tracking ID — self-attribution, skip. This catches cases
                // where the game uses different CardModel objects for the same logical card
                // (e.g. cardPlay.Card from reflection vs. the hand object).
                return false;
            }
            generatorId = proposedId;
        }
        else if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId))
        {
            generatorId = "RELIC_" + RelicExecutionManager.ExecutingRelicId;
        }
        else if (!string.IsNullOrEmpty(CurrentPlayingPotionId))
        {
            generatorId = CurrentPlayingPotionId;
        }
        else
        {
            generatorId = null;
        }

        if (string.IsNullOrEmpty(generatorId))
        {
            return false;
        }

        // Credit the ROOT generator: if the immediate generator is itself a generated card, walk up the
        // chain so the original creator accumulates the whole subtree's damage. Resolved once here and
        // cached in _cardGeneratedBy, so RouteGeneratedDamage never re-walks per damage event.
        var rootGeneratorId = ResolveRootGenerator(generatorId);
        _cardGeneratedBy[card] = rootGeneratorId;
        _cardGeneratedByImmediate[card] = generatorId;
        Log.Debug($"TryApplyGeneratorTag. Card: {card.Id.Entry} tagged to generator: {rootGeneratorId} (immediate: {generatorId})");
        return true;
    }

    // Wound/Dazed/Burn and other enemy-added status cards are not player-generated and are hidden from the
    // UI, so they are never attributed to a player source. Matches the UI's "Status" filter.
    private static bool IsStatusCard(CardModel card) => card.Type.ToString() == "Status";

    // Cards with bespoke damage/forge tracking (Sovereign Blade) must not be attributed as generated cards;
    // they keep their own row and custom forge distribution. SingletonCardIds is exactly these cards and
    // notably excludes SHIV, so genuine generated-card attribution is unaffected.
    private static bool IsGenerationAttributionExcluded(CardModel card) =>
        SingletonCardIds.Contains(card.Id.Entry ?? "");

    // When a card's identity (upgrade/enchant) changes mid-combat its tracking id changes, so its stats
    // would split across two ledger entries (e.g. the draw on the old upgrade, the play on the new). This
    // moves the previous entry's accumulated stats onto the new id so one row tracks the card across
    // mid-combat upgrades AND downgrades. Must be called under SyncRoot.
    private static void MigrateStatsOnIdentityChange(CardModel card, string newTrackingId)
    {
        if (!_cardCurrentTrackingId.TryGetValue(card, out var oldTrackingId) || oldTrackingId == newTrackingId)
        {
            _cardCurrentTrackingId[card] = newTrackingId;
            return;
        }
        _cardCurrentTrackingId[card] = newTrackingId;

        if (!EntityLedger.TryGetValue(oldTrackingId, out var oldEntity) || oldEntity is not CardStats oldStat)
        {
            return;
        }

        // Carry the per-combat encounter-seen flag so re-registration under the new id doesn't double-count.
        if (_incrementedThisCombat.Remove(oldTrackingId))
        {
            _incrementedThisCombat.Add(newTrackingId);
        }

        if (EntityLedger.TryGetValue(newTrackingId, out var newEntity) && newEntity is CardStats newStat)
        {
            MergeCardStats(newStat, oldStat);
            EntityLedger.Remove(oldTrackingId);
            Log.Debug($"MigrateStatsOnIdentityChange. Merged {oldTrackingId} into existing {newTrackingId}");
            return;
        }

        // Re-key the old entry under the new id and refresh the identity fields the id encodes.
        EntityLedger.Remove(oldTrackingId);
        oldStat.Id = newTrackingId;
        oldStat.UpgradeLevel = card.CurrentUpgradeLevel;
        oldStat.Enchantment = card.Enchantment?.Id.Entry ?? "";
        oldStat.BaseCardKey = GetBaseCardKey(card);
        oldStat.DisplayName = card.Title ?? card.Id.Entry ?? oldStat.DisplayName;
        EntityLedger[newTrackingId] = oldStat;
        Log.Debug($"MigrateStatsOnIdentityChange. Renamed {oldTrackingId} -> {newTrackingId}");
    }

    // A deck-master object that was previously removed has reappeared in the deck under a new tracking
    // id (its FloorAddedToDeck was rewritten on re-add, e.g. Thieving Hopper return). Re-key its
    // original entry onto the new id so its accumulated stats survive as one row, preserving the
    // original FloorAdded/BaseCardKey so the overlay still shows the true add-floor and merges with
    // any un-stolen siblings. Must be called under SyncRoot.
    private static void TryReviveReturnedDeckCard(CardModel sourceCard, string newTrackingId)
    {
        if (!_removedDeckCardIds.TryGetValue(sourceCard, out var oldTrackingId))
        {
            return;
        }
        _removedDeckCardIds.Remove(sourceCard);
        if (oldTrackingId == newTrackingId)
        {
            return; // same id -> SyncDeckState's refresh branch already clears the removal markers
        }
        if (!EntityLedger.TryGetValue(oldTrackingId, out var oldEntity) || oldEntity is not CardStats oldStat)
        {
            return;
        }

        // A separate entry already exists under the new id (rare id collision) -> fold stats in, but keep
        // the original add-floor identity on the survivor.
        if (EntityLedger.TryGetValue(newTrackingId, out var newEntity) && newEntity is CardStats newStat)
        {
            MergeCardStats(newStat, oldStat);
            newStat.FloorAdded = oldStat.FloorAdded;
            newStat.BaseCardKey = oldStat.BaseCardKey;
            EntityLedger.Remove(oldTrackingId);
            Log.Debug($"TryReviveReturnedDeckCard. Merged {oldTrackingId} into {newTrackingId}");
            return;
        }

        // Re-key the removed entry onto the new id and clear the removal markers. FloorAdded and
        // BaseCardKey are intentionally left at their original (pre-theft) values.
        EntityLedger.Remove(oldTrackingId);
        oldStat.Id = newTrackingId;
        oldStat.FloorRemoved = -1;
        oldStat.FloorLeftDeck = -1;
        oldStat.IsActive = true;
        oldStat.CopiesInDeck = 1; // SyncDeckState re-counts copies immediately after this scan
        EntityLedger[newTrackingId] = oldStat;
        Log.Debug($"TryReviveReturnedDeckCard. Revived {oldTrackingId} -> {newTrackingId} (FloorAdded kept {oldStat.FloorAdded})");
    }

    // Folds one card's accumulated stats into another (used when a mid-combat identity change lands on an
    // id that already exists). Must be called under SyncRoot.
    private static void MergeCardStats(CardStats target, CardStats source)
    {
        target.CombatDamage += source.CombatDamage;
        target.RunDamage += source.RunDamage;
        target.GeneratedCombatDamage += source.GeneratedCombatDamage;
        target.GeneratedRunDamage += source.GeneratedRunDamage;
        target.CombatTimesDrawn += source.CombatTimesDrawn;
        target.CombatTimesPlayed += source.CombatTimesPlayed;
        target.RawForgeCombat += source.RawForgeCombat;
        target.ConnectedForgeCombat += source.ConnectedForgeCombat;
        target.ReceivedForgeCombat += source.ReceivedForgeCombat;
        target.Act1.Add(source.Act1);
        target.Act2.Add(source.Act2);
        target.Act3.Add(source.Act3);
        target.Act4.Add(source.Act4);
    }

    // Walks GeneratedById links through the ledger up to the first non-generated source, so the root
    // generator gets credit for a chain of generated cards (G -> A -> B all credit G). Bounded loop guards
    // against any accidental cycle. Must be called under SyncRoot.
    private static string ResolveRootGenerator(string generatorId)
    {
        var current = generatorId;
        for (var guard = 0; guard < 16; guard++)
        {
            if (!EntityLedger.TryGetValue(current, out var entity)
                || entity is not CardStats stat
                || string.IsNullOrEmpty(stat.GeneratedById)
                || stat.GeneratedById == current)
            {
                break;
            }
            current = stat.GeneratedById;
        }
        return current;
    }

    // Deck cards: deterministic ordinal per (owner, identity), recomputed each scan so the id set is always
    // C0..C(N-1) for N copies — stable across client re-syncs (no churn-induced duplicate/EVOLVED rows).
    private static void AssignDeckCopyIndex(CardModel sourceCard, string ownerNetId)
    {
        var baseId = sourceCard.Id.Entry ?? "Unknown";
        if (SingletonCardIds.Contains(baseId))
        {
            _cardInstanceIds[sourceCard] = 0;
            return;
        }
        var floor = sourceCard.FloorAddedToDeck ?? 0;
        var upgrade = sourceCard.CurrentUpgradeLevel;
        var enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        var key = $"{ownerNetId}|{baseId}_F{floor}_U{upgrade}_{enchant}";
        _deckScanOrdinals.TryGetValue(key, out var ordinal);
        _cardInstanceIds[sourceCard] = ordinal;
        _deckScanOrdinals[key] = ordinal + 1;
    }

    // Generated/combat cards (not part of a deck scan): assign the next copy index from a persistent
    // per-{baseId}_F{floor} counter so each generated copy is a distinct entry (auto-stacked in the UI).
    private static int GetOrAssignCopyIndex(CardModel sourceCard)
    {
        if (_cardInstanceIds.TryGetValue(sourceCard, out var existing))
        {
            return existing;
        }
        var baseId = sourceCard.Id.Entry ?? "Unknown";
        if (SingletonCardIds.Contains(baseId))
        {
            _cardInstanceIds[sourceCard] = 0;
            return 0;
        }
        var floor = sourceCard.FloorAddedToDeck ?? 0;
        var key = $"{baseId}_F{floor}";
        _cardInstanceCounters.TryGetValue(key, out var counter);
        _cardInstanceIds[sourceCard] = counter;
        _cardInstanceCounters[key] = counter + 1;
        return counter;
    }

    // Identity is keyed by owning player's NetId instead of a per-physical-copy index, so it is a pure
    // function of deck composition + owner and stays stable across multiplayer client re-syncs.
    // ownerNetId may be passed by the deck scan (which knows the player); otherwise it is resolved from the card.
    public static string GetTrackingId(CardModel? card, string? ownerNetId = null)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = ResolveSourceCard(card);

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";
        int copyIndex = GetCopyIndex(sourceCard);
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);

        return $"{baseId}_F{floorAdded}_C{copyIndex}_U{upgradeLevel}_{enchant}_P{owner}";
    }

    public static string GetBaseCardKey(CardModel? card, string? ownerNetId = null)
    {
        if (card == null)
        {
            return "";
        }
        CardModel sourceCard = ResolveSourceCard(card);
        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        string owner = ownerNetId ?? ResolveOwnerNetId(sourceCard);
        return $"{baseId}_F{floorAdded}_P{owner}";
    }

    // Resolves the active source ID: card first, then executing relic, then active potion, then fallback.
    public static string GetCurrentSourceId(CardModel? cardSource = null, string fallback = "External_Source")
    {
        if (cardSource != null) return GetTrackingId(cardSource);
        if (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId)) return "RELIC_" + RelicExecutionManager.ExecutingRelicId;
        if (!string.IsNullOrEmpty(CurrentPlayingPotionId)) return CurrentPlayingPotionId;
        return fallback;
    }
}
