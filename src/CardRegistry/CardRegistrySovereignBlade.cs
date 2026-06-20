using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<DamageHistoryItem> SovereignBladeDamageHistory = [];
    private static readonly Dictionary<Creature, Queue<string>> ConquerorTracker = [];
    private static CardModel? _activeSeekingEdgeCard;
    private static readonly List<CardModel?> BladeReplayModifierTracker = [];
    // Returns true if the card has custom damage handling so the caller skips the default AddDamage path.
    public static bool TryHandleCustomCardDamage(ICombatState combatState, Creature? dealer, DamageResult results, Creature target, CardModel cardSource, decimal baseDmg)
    {
        switch (cardSource.Id.Entry)
        {
            case "SOVEREIGN_BLADE":
                var item = new DamageHistoryItem(combatState, dealer, results, target, cardSource, baseDmg)
                {
                    ActiveConquerorId = GetEarliestActiveConqueror(target)
                };
                AddSovereignBladeDamageHistoryItem(item);
                return true;
            case "SHIV":
                AddShivDamage(cardSource, baseDmg);
                return true;
            default:
                return false;
        }
    }

    public static void ResetSovereignBladeState()
    {
        lock (SyncRoot)
        {
            SovereignBladeDamageHistory.Clear();
            ConquerorTracker.Clear();
            BladeReplayModifierTracker.Clear();
            _activeSeekingEdgeCard = null;
            Log.Debug("ResetSovereignBladeState. All sovereign blade state cleared.");
        }
    }

    public static void AddSovereignBladeDamageHistoryItem(DamageHistoryItem damageHistoryItem)
    {
        SovereignBladeDamageHistory.Add(damageHistoryItem);
    }
    
    public static void UpdateSeekingEdge(CardModel seekingEdgeCard)
    {
        _activeSeekingEdgeCard ??= seekingEdgeCard;
        Log.Debug($"Updated active seeking edge: {GetTrackingId(_activeSeekingEdgeCard)}.");
    }
    
    public static void UpdateConquerorTracker(Creature target, decimal powerChangeAmount, CardModel? cardSource)
    {
        if (!ConquerorTracker.ContainsKey(target)) ConquerorTracker[target] = new Queue<string>();
        var conquerorQueue = ConquerorTracker[target];
        switch (powerChangeAmount)
        {
            case > 0:
                if (cardSource != null)
                {
                    var uniqueTrackingId = GetTrackingId(cardSource);
                    conquerorQueue.Enqueue(uniqueTrackingId);
                    Log.Debug($"UpdateConquerorTracker. Conqueror card queued. ID: {uniqueTrackingId}");
                }
                break;
            case < 0:
                if (conquerorQueue.Count > 0)
                {
                    var dequeued = conquerorQueue.Dequeue();
                    Log.Debug($"UpdateConquerorTracker. Conqueror card dequeued. ID: {dequeued}");
                }
                else
                {
                    Log.Warn("UpdateConquerorTracker. Attempted to dequeue from empty Conqueror queue.");
                }
                break;
        }
        
        Publish();
    }
    
    private static string? GetEarliestActiveConqueror(Creature target)
    {
        return ConquerorTracker.TryGetValue(target, out var conquerorQueue) && conquerorQueue.Count > 0
            ? conquerorQueue.Peek()
            : null;
    }
    
    // For SwordSage, replayCountAdded would be 1. Later when supporting Hidden Gem hitting this, it may be 2 or 3
    public static void UpdateSovereignBladeReplayModifierTracker(decimal replayCountAdded, CardModel? cardSource)
    {
        if (cardSource == null || replayCountAdded <= 0)
        {
            Publish();
            return;
        }

        var uniqueTrackingId = GetTrackingId(cardSource);
        for (var i = 0; i < (int)replayCountAdded; i++)
        {
            Log.Debug($"Adding replay modifier to history with ID {uniqueTrackingId}.");
            BladeReplayModifierTracker.Add(cardSource);
        }
        Publish();
    }
    
    private static void SplitForgeDamage(CardModel bladeCard, DamageResult results, decimal baseDamage, Creature target, CardModel? seekingEdge, CardModel? replayModifyingCard, string? conquerorId)
    {
        lock (SyncRoot)
        {
            Log.VeryDebug($"ConquerorID: {conquerorId}.");
            // Split the card-intrinsic base damage (modifiers like Strength/Vigor/Vulnerable have already
            // been paid out to their own sources by ProcessDamageSnapshot), not Results.TotalDamage, or
            // those modifiers would be double-counted onto the blade.
            var damageToAttribute = baseDamage;
            Log.Debug($"SplitForgeDamage. Damage to attribute: {damageToAttribute}.");

            if (conquerorId != null)
            {
                var completeDamage = results.TotalDamage + results.OverkillDamage;
                var beforeMultiplicationDamage = completeDamage / 2;
                var damageToAttributeToConqueror = results.TotalDamage - beforeMultiplicationDamage;

                if (EntityLedger.TryGetValue(conquerorId, out var conquerorEntity))
                {
                    Log.Debug($"Attributing damage to {conquerorId}");
                    conquerorEntity.AddCombatDamage(damageToAttributeToConqueror, _currentAct, _currentCombatType);
                }

                damageToAttribute -= damageToAttributeToConqueror;
            }

            // Conqueror damage is skimmed off the top before everything else, then the natural damage is processed.
            // Order is Sword Sage favored (vs Seeking Edge) in damage evaluation order.
            if (replayModifyingCard != null)
            {
                // REPLAY HIT: the modifier card caused this new swing — it gets all the credit.
                Log.Debug($"Replay Hit: Attributing {damageToAttribute} to {GetTrackingId(replayModifyingCard)}");
                AddDamage(replayModifyingCard, damageToAttribute);
            }
            else if (seekingEdge != null)
            {
                // SPILLOVER HIT: damage goes to Seeking Edge, not the blade or forgers.
                AddDamage(seekingEdge, damageToAttribute);
            }
            else
            {
                // MAX HIT: base damage to the blade, remainder distributed across forge history.
                AttributeMaxHitDamage(bladeCard, damageToAttribute);
            }
        }
        Publish();
    }

    // Distributes the max-hit damage across the blade and its forge contributors.
    // Note: forgers always receive connected-forge credit, but the blade absorbs the raw damage number
    // (which the UI then offsets with ReceivedForgeCombat). This is a deliberate compromise that avoids
    // complex per-debuff proportional rounding — see inline comment history for details.
    private static void AttributeMaxHitDamage(CardModel bladeCard, decimal damageToAttribute)
    {
        const int sovereignBladeBaseDamage = 10;
        AddDamage(bladeCard, Math.Min(damageToAttribute, sovereignBladeBaseDamage));
        damageToAttribute -= sovereignBladeBaseDamage;

        var sourceModel = ResolveSourceCard(bladeCard);
        var (totalDistributed, remaining) = DistributeForgeHistory(Math.Max(0, damageToAttribute), sourceModel);

        if (remaining > 0)
        {
            AddDamage(bladeCard, remaining);
            Log.Warn($"AttributeMaxHitDamage. Went through all forgers with remaining damage: {remaining}.");
        }

        AddDamage(bladeCard, totalDistributed);
        Log.Debug($"Adding total distributed forge damage amount {totalDistributed} to Blade.");

        if (totalDistributed <= 0) return;

        var bladeTrackingId = GetTrackingId(bladeCard);
        if (!EntityLedger.TryGetValue(bladeTrackingId, out var bladeEntity)) return;

        bladeEntity.ReceivedForgeCombat += totalDistributed;
        bladeEntity.GetAct(_currentAct)?.AddReceivedForge(_currentCombatType, totalDistributed);
    }

    private static (decimal distributed, decimal remaining) DistributeForgeHistory(decimal damageToDistribute, CardModel bladeSourceModel)
    {
        if (!BladeForgeHistories.TryGetValue(bladeSourceModel, out var history))
        {
            Log.Warn($"DistributeForgeHistory. No forge history for blade: {GetTrackingId(bladeSourceModel)}. Forge damage unattributed.");
            return (0, damageToDistribute);
        }
        decimal totalDistributed = 0;
        foreach (var forgeInstance in history)
        {
            if (damageToDistribute <= 0) break;

            var amount = Math.Min(damageToDistribute, forgeInstance.Amount);
            damageToDistribute -= amount;
            totalDistributed += amount;

            if (!EntityLedger.TryGetValue(forgeInstance.TrackingId, out var entity)) continue;

            Log.Debug($"Adding connected forge to {forgeInstance.TrackingId} with amount {amount}");
            entity.ConnectedForgeCombat += amount;
            entity.GetAct(_currentAct)?.AddConnectedForge(_currentCombatType, amount);
        }
        return (totalDistributed, damageToDistribute);
    }
    
    
    public static void ProcessSovereignBladeHistory(CardPlay cardPlay)
    {
        if (SovereignBladeDamageHistory.Count == 0) return;
        var bladeCardModel = SovereignBladeDamageHistory[0].CardModel;
        if (bladeCardModel == null)
        {
            Log.Warn("ProcessSovereignBladeHistory. bladeCardModel is null.");
            return;
        }
        var expectedReplayCount = bladeCardModel.BaseReplayCount;
        
        Log.Debug($"Processing sovereign blade history with count: {SovereignBladeDamageHistory.Count}");
        // 0 is primary play, 1+ means replayed
        var isReplay = cardPlay.PlayIndex > 0;
        Log.Debug($"ProcessSovereignBladeHistory. IsReplay: {isReplay}, PlayIndex: {cardPlay.PlayIndex}");
        
        var maxTotalDamageInstance = SovereignBladeDamageHistory[0];
        foreach (var damageHistoryItem in SovereignBladeDamageHistory)
        {
            if (damageHistoryItem.Results.TotalDamage > maxTotalDamageInstance.Results.TotalDamage)
            {
                maxTotalDamageInstance = damageHistoryItem;
            }
        }

        foreach (var damageHistoryItem in SovereignBladeDamageHistory)
        {
            if (damageHistoryItem.CardModel == null)
            {
                Log.Warn("ProcessSovereignBladeHistory. damageHistoryItem.CardModel is null.");
                continue;
            }

            CardModel? replayModifyingCard = null;
            if (isReplay)
            {
                // E.g. the first replay is caused by the modifier at index 0 in _bladeReplayModifierTracker
                var trackerIndex = cardPlay.PlayIndex - 1;
                
                // In case a potion/relic caused a replay that we didn't track, it will default to giving
                // the extra replay damage to the blade and its forgers
                if (trackerIndex < BladeReplayModifierTracker.Count)
                {
                    replayModifyingCard = BladeReplayModifierTracker[trackerIndex];
                }
                else
                {
                    Log.Warn("ProcessSovereignBladeHistory. trackerIndex out of bounds; a replay modifier was likely not tracked.");
                }
            }
            // Biggest damage goes to the blade and its forgers, all other hits if AOE are attributed to Seeking Edge
            if (damageHistoryItem == maxTotalDamageInstance)
            {
                Log.Debug($"ProcessSovereignBladeHistory. Max-damage hit; attributing to blade and forgers (no seeking edge).");
                SplitForgeDamage(damageHistoryItem.CardModel, damageHistoryItem.Results, damageHistoryItem.BaseDamage, damageHistoryItem.Target, null, replayModifyingCard, damageHistoryItem.ActiveConquerorId);
            }
            else
            {
                Log.Debug($"ProcessSovereignBladeHistory. Attributing to Seeking Edge: {GetTrackingId(_activeSeekingEdgeCard)}.");
                SplitForgeDamage(damageHistoryItem.CardModel, damageHistoryItem.Results, damageHistoryItem.BaseDamage, damageHistoryItem.Target, _activeSeekingEdgeCard, replayModifyingCard, damageHistoryItem.ActiveConquerorId);
            }
        }
        
        // Clear after processing all active hits
        SovereignBladeDamageHistory.Clear();
    }
    
}