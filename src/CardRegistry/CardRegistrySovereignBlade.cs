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
                AddSovereignBladeDamageHistoryItem(new DamageHistoryItem(combatState, dealer, results, target, cardSource));
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
            GD.Print("[DeckTracker] ResetSovereignBladeState. All sovereign blade state cleared.");
        }
    }

    public static void AddSovereignBladeDamageHistoryItem(DamageHistoryItem damageHistoryItem)
    {
        SovereignBladeDamageHistory.Add(damageHistoryItem);
    }
    
    public static void UpdateSeekingEdge(CardModel seekingEdgeCard)
    {
        _activeSeekingEdgeCard ??= seekingEdgeCard;
        GD.Print($"[DeckTracker] Updated active seeking edge: {GetTrackingId(_activeSeekingEdgeCard)}.");
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
                    GD.Print($"[DeckTracker] Conqueror card queued with id {uniqueTrackingId}");
                }
                break;
            case < 0:
                var dequeued = conquerorQueue.Dequeue();
                GD.Print($"[DeckTracker] Conqueror card dequeued with id {dequeued}");
                break;
        }
        
        Publish();
    }
    
    private static string? GetEarliestActiveConqueror(Creature target)
    {
        return ConquerorTracker.TryGetValue(target, out var conquerorQueue) ? conquerorQueue.Peek() : null;
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
            GD.Print($"[DeckTracker] Adding replay modifier to history with ID {uniqueTrackingId}.");
            BladeReplayModifierTracker.Add(cardSource);
        }
        Publish();
    }
    
    private static void SplitForgeDamage(CardModel bladeCard, DamageResult results, Creature target, CardModel? seekingEdge, CardModel? replayModifyingCard)
    {
        lock (SyncRoot)
        {
            var conquerorId = GetEarliestActiveConqueror(target);
            GD.Print($"[DeckTracker] ConquerorID requested: {conquerorId}.");
            var damageToAttribute = results.TotalDamage;
            GD.Print($"[DeckTracker] Damage to attribute: {damageToAttribute}.");

            if (conquerorId != null)
            {
                var completeDamage = results.TotalDamage + results.OverkillDamage;
                var beforeMultiplicationDamage = completeDamage / 2;
                var damageToAttributeToConqueror = results.TotalDamage - beforeMultiplicationDamage;

                if (EntityLedger.TryGetValue(conquerorId, out var conquerorEntity))
                {
                    GD.Print($"[DeckTracker] Attributing damage to {conquerorId}");
                    conquerorEntity.AddCombatDamage(damageToAttributeToConqueror, _currentAct, _currentCombatType);
                }

                damageToAttribute -= damageToAttributeToConqueror;
            }

            // Conqueror damage is skimmed off the top before everything else, then the natural damage is processed.
            // Order is Sword Sage favored (vs Seeking Edge) in damage evaluation order.
            if (replayModifyingCard != null)
            {
                // REPLAY HIT: the modifier card caused this new swing — it gets all the credit.
                GD.Print($"[DeckTracker] Replay Hit: Attributing {damageToAttribute} to {GetTrackingId(replayModifyingCard)}");
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

        var (totalDistributed, remaining) = DistributeForgeHistory(Math.Max(0, damageToAttribute));

        if (remaining > 0)
        {
            AddDamage(bladeCard, remaining);
            GD.Print($"[DeckTracker] Warning: Went through all forgers and had remaining damage: {remaining}.");
        }

        AddDamage(bladeCard, totalDistributed);
        GD.Print($"[DeckTracker] Adding total distributed forge damage amount {totalDistributed} to Blade.");

        if (totalDistributed <= 0) return;

        var bladeTrackingId = GetTrackingId(bladeCard);
        if (!EntityLedger.TryGetValue(bladeTrackingId, out var bladeEntity)) return;

        bladeEntity.ReceivedForgeCombat += totalDistributed;
        bladeEntity.GetAct(_currentAct)?.AddReceivedForge(_currentCombatType, totalDistributed);
    }

    private static (decimal distributed, decimal remaining) DistributeForgeHistory(decimal damageToDistribute)
    {
        decimal totalDistributed = 0;
        foreach (var forgeInstance in ForgeHistory)
        {
            if (damageToDistribute <= 0) break;

            var amount = Math.Min(damageToDistribute, forgeInstance.Amount);
            damageToDistribute -= amount;
            totalDistributed += amount;

            if (!EntityLedger.TryGetValue(forgeInstance.TrackingId, out var entity)) continue;

            GD.Print($"[DeckTracker] Adding connected forge to {forgeInstance.TrackingId} with amount {amount}");
            entity.ConnectedForgeCombat += amount;

            // Relics don't track act-level forge breakdowns.
            if (entity is not RelicStats)
            {
                entity.GetAct(_currentAct)?.AddConnectedForge(_currentCombatType, amount);
            }
        }
        return (totalDistributed, damageToDistribute);
    }
    
    
    public static void ProcessSovereignBladeHistory(CardPlay cardPlay)
    {
        if (SovereignBladeDamageHistory.Count == 0) return;
        var bladeCardModel = SovereignBladeDamageHistory[0].CardModel;
        if (bladeCardModel == null)
        {
            GD.Print("[DeckTracker] Warning: bladeCardModel is null in ProcessSovereignBladeHistory");
            return;
        }
        var expectedReplayCount = bladeCardModel.BaseReplayCount;
        
        GD.Print($"[DeckTracker] Processing sovereign blade history with count: {SovereignBladeDamageHistory.Count}");
        // 0 is primary play, 1+ means replayed
        var isReplay = cardPlay.PlayIndex > 0;
        GD.Print($"[DeckTracker] isReplay: {isReplay} (PlayIndex: {cardPlay.PlayIndex})");
        
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
                GD.Print("[DeckTracker] Warning: damageHistoryItem.CardModel is null in ProcessSovereignBladeHistory");
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
                    GD.Print("[DeckTracker] Warning: trackerIndex was out of bounds. A replay modifier was likely not tracked");
                }
            }
            // Biggest damage goes to the blade and its forgers, all other hits if AOE are attributed to Seeking Edge
            if (damageHistoryItem == maxTotalDamageInstance)
            {
                GD.Print($"[DeckTracker] Max damage item. Sending with seeking edge null");
                SplitForgeDamage(damageHistoryItem.CardModel, damageHistoryItem.Results, damageHistoryItem.Target, null, replayModifyingCard);
            }
            else
            {
                GD.Print($"[DeckTracker] Attributing to seeking, with {_activeSeekingEdgeCard} as seeking edge ID");
                SplitForgeDamage(damageHistoryItem.CardModel, damageHistoryItem.Results, damageHistoryItem.Target, _activeSeekingEdgeCard, replayModifyingCard);
            }
        }
        
        // Clear after processing all active hits
        SovereignBladeDamageHistory.Clear();
    }
    
}