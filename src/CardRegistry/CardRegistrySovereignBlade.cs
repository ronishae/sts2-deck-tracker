using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<ForgeInstance> ForgeHistory = new();
    private static readonly List<DamageHistoryItem> SovereignBladeDamageHistory = [];
    private static readonly Dictionary<Creature, Queue<string>> ConquerorTracker = [];
    private static CardModel? _activeSeekingEdgeCard;
    private static readonly List<CardModel?> BladeReplayModifierTracker = [];
    private static readonly List<FurnaceContribution> FurnaceContributions = [];
    
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
    
    // 2. The Universal Forge Router
    public static void AddForgeById(string trackingId, decimal amount)
    {
        lock (SyncRoot)
        {
            // A. Is it a Relic?
            if (trackingId.StartsWith("RELIC_"))
            {
                string relicId = trackingId.Substring(6);
                AddRelicForge(relicId, amount, 0, 0); // Credit Raw Forge
                ForgeHistory.Add(new ForgeInstance { TrackingId = trackingId, Amount = amount });
            }
            // B. Is it a Card?
            else if (Totals.TryGetValue(trackingId, out var stat))
            {
                stat.RawForgeCombat += amount;
                
                var actData = GetActData(stat, _currentAct);
                if (actData != null)
                {
                    if (_currentCombatType == "Elite") actData.RawForgeElite += amount;
                    else if (_currentCombatType == "Boss") actData.RawForgeBoss += amount;
                    else if (_currentCombatType == "Hallway") actData.RawForgeHallway += amount;
                }
                ForgeHistory.Add(new ForgeInstance { TrackingId = trackingId, Amount = amount });
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
            // Since Furnace power generally doesn't decrease, we only track the additions
            // in the exact order they are played.
            if (amount > 0)
            {
                FurnaceContributions.Add(new FurnaceContribution {
                    CardSource = cardSource,
                    PowerAmount = amount
                });
                GD.Print($"[DeckTracker] Furnace contribution added: {GetTrackingId(cardSource)} for {amount} power.");
            }
        }
        Publish();
    }

    public static void HandleFurnaceForge(decimal forgeAmount)
    {
        if (forgeAmount <= 0) return;
        
        List<(CardModel card, decimal amount)> attributions = new();

        lock (SyncRoot)
        {
            decimal remainingForge = forgeAmount;

            foreach (var contribution in FurnaceContributions)
            {
                if (remainingForge <= 0) break;

                decimal amountToAttribute = Math.Min(remainingForge, contribution.PowerAmount);
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
        if (cardSource != null && replayCountAdded > 0)
        {
            var uniqueTrackingId = GetTrackingId(cardSource);
            for (var i = 0; i < (int)replayCountAdded; i++)
            {
                GD.Print($"[DeckTracker] Adding replay modifier to history with ID {uniqueTrackingId}.");
                BladeReplayModifierTracker.Add(cardSource);
            }
        }
        Publish();
    }
    
    private static void SplitForgeDamage(CardModel bladeCard, DamageResult results, Creature target, CardModel? seekingEdge, CardModel? replayModifyingCard)
    {
        lock (SyncRoot)
        {
            const int sovereignBladeBaseDamage = 10;
             
            var conquerorId = GetEarliestActiveConqueror(target);
            GD.Print($"[DeckTracker] ConquerorID requested: {conquerorId}.");
            var damageToAttribute = results.TotalDamage;
            GD.Print($"[DeckTracker] Damage to attribute: {damageToAttribute}.");
            if (conquerorId != null)
            {
                var completeDamage = results.TotalDamage + results.OverkillDamage;
                var beforeMultiplicationDamage = completeDamage / 2;
                var damageToAttributeToConqueror = results.TotalDamage - beforeMultiplicationDamage;
                
                if (Totals.TryGetValue(conquerorId, out var stat))
                {
                    GD.Print($"[DeckTracker] Attributing damage to {conquerorId}");
                    stat.CombatDamage += damageToAttributeToConqueror;
                    stat.RunDamage += damageToAttributeToConqueror;
                    
                    var actData = GetActData(stat, _currentAct);
                    if (actData != null)
                    {
                        if (_currentCombatType == "Elite") actData.DamageElite += damageToAttributeToConqueror;
                        else if (_currentCombatType == "Boss") actData.DamageBoss += damageToAttributeToConqueror;
                        else if (_currentCombatType == "Hallway") actData.DamageHallway += damageToAttributeToConqueror;
                    }
                }
                
                damageToAttribute -= damageToAttributeToConqueror;
            }
            
            // Conqueror damage is skimmed off the top before everything else, then the natural damage is processed
            // Order to be Sword Sage favored (vs Seeking Edge) in damage evaluation order
            if (replayModifyingCard != null)
            {
                // REPLAY HIT (Both Max and Spillover)
                // The modifier card caused this entirely new swing to happen.
                // It gets damage on the max hit and all AOE hits,
                // even though the AOE would not be possible without Seeking Edge.
                // This could be changed later to split damage between both if both are present,
                // but to me, Sword Sage feels like it should get all the credit (the number looks very small without this)
                GD.Print($"[DeckTracker] Replay Hit: Attributing {damageToAttribute} to {GetTrackingId(replayModifyingCard)}");
                AddDamage(replayModifyingCard, damageToAttribute);
            }
            else if (seekingEdge != null) 
            {
                // SPILLOVER HIT
                // Damage goes to Seeking Edge, not the blade or forgers
                AddDamage(seekingEdge, damageToAttribute);
            }
            else
            {
                // MAX HIT
                // Base damage goes to the blade.
                // Note: In the future, may want to change how this handles weak / shrunk.
                // The current implementation gives damage to base blade first before all forgers,
                // so when weak, the blade will eat all the damage from the forgers, even though
                // forgers actually added damage to the blade.
                // The issue with proportional rounding (e.g. blade and forgers get 0.75%) penalty
                // is 1) because of a lot of messy interactions with flooring in STS, 2) it would mess up
                // interactions with truncated damage caps (e.g. intangible, Exoskeletons, maybe Skulking Colony),
                // 3) detecting the specific debuff (weak vs shrunk) on the player is annoying (but possible, I guess)
                // For now, just leave it at this compromise to penalize forgers when the player is weak / small.
                AddDamage(bladeCard, Math.Min(damageToAttribute, sovereignBladeBaseDamage));
                damageToAttribute -= sovereignBladeBaseDamage;

                decimal damageToDistribute = Math.Max(0, damageToAttribute);
                decimal totalDistributed = 0;

                foreach (var forgeInstance in ForgeHistory)
                {
                    if (damageToDistribute <= 0) break;

                    var amountToAttribute = Math.Min(damageToDistribute, forgeInstance.Amount);
                    var idToAttribute = forgeInstance.TrackingId;
                    
                    if (idToAttribute.StartsWith("RELIC_"))
                    {
                        string relicId = idToAttribute.Substring(6);
                        GD.Print($"[DeckTracker] Adding connected forge to Relic {relicId} with amount {amountToAttribute}");
                        AddRelicForge(relicId, 0, amountToAttribute, 0);
                    }
                    else if (Totals.TryGetValue(idToAttribute, out var stat))
                    {
                        GD.Print($"adding connected forge to {idToAttribute} with amount {amountToAttribute}");
                        stat.ConnectedForgeCombat += amountToAttribute;
                        
                        var actData = GetActData(stat, _currentAct);
                        if (actData != null)
                        {
                            if (_currentCombatType == "Elite") actData.ConnectedForgeElite += amountToAttribute;
                            else if (_currentCombatType == "Boss") actData.ConnectedForgeBoss += amountToAttribute;
                            else if (_currentCombatType == "Hallway") actData.ConnectedForgeHallway += amountToAttribute;
                        }
                    }

                    damageToDistribute -= amountToAttribute;
                    totalDistributed += amountToAttribute;
                }
                
                // Fallback for forges from unknown sources (e.g. Fencing Manual before relic support is added)
                if (damageToDistribute > 0)
                {
                    AddDamage(bladeCard, damageToDistribute);
                    GD.Print(
                        $"[DeckTracker] Warning: Went through all forgers and had remaining damage: {damageToDistribute}.");
                }

                // Although we distribute forge amounts to forgers, the damage goes to the blade initially
                // Then this damage can be subtracted by the forge received when using the forge view.
                AddDamage(bladeCard, totalDistributed);
                GD.Print(
                    $"[DeckTracker] Adding total distributed forge damage amount {totalDistributed} to Blade.");
                if (totalDistributed > 0)
                {
                    string bladeTrackingId = GetTrackingId(bladeCard);
                    if (Totals.TryGetValue(bladeTrackingId, out CardStats? bladeStat))
                    {
                        bladeStat.ReceivedForgeCombat += totalDistributed;
                        
                        var actData = GetActData(bladeStat, _currentAct);
                        if (actData != null)
                        {
                            if (_currentCombatType == "Elite") actData.ReceivedForgeElite += totalDistributed;
                            else if (_currentCombatType == "Boss") actData.ReceivedForgeBoss += totalDistributed;
                            else if (_currentCombatType == "Hallway") actData.ReceivedForgeHallway += totalDistributed;
                        }
                    }
                }
            }
        }
        Publish();
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

public class FurnaceContribution
{
    public CardModel CardSource { get; init; } = null!;
    public decimal PowerAmount { get; init; }
}