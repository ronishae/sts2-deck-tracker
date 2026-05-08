using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class ForgeInstance
{
    public string TrackingId { get; set; } = "";
    public decimal Amount { get; set; }
}

public static class CardRegistry
{
    private static readonly object SyncRoot = new();
    
    private static Dictionary<string, CardStats> Totals = new();
    private static string _currentRunSeed = "";
    
    private static List<ForgeInstance> _forgeHistory = new();
    private static List<DamageHistoryItem> _sovereignBladedamageHistory = [];
    private static Dictionary<Creature, Queue<string>> _conquerorTracker = [];
    private static CardModel? _activeSeekingEdgeCard = null;
    private static List<CardModel?> _bladeReplayModifierTracker = [];
    
    // NEW: We need to know what fight we are in while dealing damage!
    private static string _currentCombatType = "Unknown";
    
    // Tracks which cards have already received their +1 encounter this specific combat
    private static HashSet<string> _incrementedThisCombat = new();
    
    private static readonly string SavePath = ProjectSettings.GlobalizePath("user://deck_tracker_save.json");

    public static event Action<List<CardStats>>? Changed;

    // --- Persistence Logic ---

    public static void SyncRun(string runSeed)
    {
        if (string.IsNullOrEmpty(runSeed)) return;

        lock (SyncRoot)
        {
            if (_currentRunSeed == runSeed) return;

            _currentRunSeed = runSeed;

            if (TryLoadState(runSeed))
            {
                GD.Print($"[DeckTracker] Successfully resumed run data for seed: {runSeed}");
            }
            else
            {
                GD.Print($"[DeckTracker] Starting fresh tracker for new run seed: {runSeed}");
                Totals.Clear();
            }
        }
        Publish();
    }

    public static void SaveState()
    {
        try
        {
            SavedRunState state;
            lock (SyncRoot)
            {
                if (string.IsNullOrEmpty(_currentRunSeed)) return;

                state = new SavedRunState
                {
                    RunSeed = _currentRunSeed,
                    Totals = Totals.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Clone())
                };
            }

            string json = JsonSerializer.Serialize(state, SavedStateCtx.Default.SavedRunState);
            System.IO.File.WriteAllText(SavePath, json);
        }
        catch (Exception e)
        {
            GD.PrintErr($"[DeckTracker] Failed to save state: {e.Message}");
        }
    }

    private static bool TryLoadState(string targetSeed)
    {
        try
        {
            if (!System.IO.File.Exists(SavePath)) return false;

            string json = System.IO.File.ReadAllText(SavePath);
            SavedRunState? state = JsonSerializer.Deserialize(json, SavedStateCtx.Default.SavedRunState);
            
            if (state == null || state.RunSeed != targetSeed) return false;

            Totals = state.Totals;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // --- Fingerprint Generation ---

    public static string GetTrackingId(CardModel card)
    {
        CardModel sourceCard = card.DeckVersion ?? card;

        string baseId = sourceCard.Id.Entry ?? "Unknown";
        int floorAdded = sourceCard.FloorAddedToDeck ?? 0;
        int upgradeLevel = sourceCard.CurrentUpgradeLevel;
        string enchant = sourceCard.Enchantment?.Id.Entry ?? "None";

        return $"{baseId}_F{floorAdded}_U{upgradeLevel}_{enchant}";
    }

    // --- Combat Lifecycle ---

    public static void ResetRun()
    {
        lock (SyncRoot)
        {
            Totals.Clear();
            _forgeHistory.Clear();
        }
        Publish();
    }
    
    // NEW: Handles the diff check for Upgrades, Transforms, and Removes
    public static void SyncDeckState(int currentFloor, List<string> activeDeckIds)
    {
        lock (SyncRoot)
        {
            var copyCounts = activeDeckIds.GroupBy(id => id).ToDictionary(g => g.Key, g => g.Count());
            
            HashSet<string> uniqueActiveIds = new HashSet<string>(activeDeckIds);
            
            GD.Print($"[DeckTracker] {activeDeckIds}");
            foreach (var stat in Totals.Values)
            {
                // DIFF CHECK: If we think the card is in the deck, but the game scan didn't find it
                if (stat.IsInDeck && !uniqueActiveIds.Contains(stat.CardId))
                {
                    GD.Print($"[DeckTracker] {stat.CardId} is gone");
                    stat.IsInDeck = false;
                    stat.CopiesInDeck = 0;
                        
                    int floorLeft = Math.Max(1, currentFloor - 1);
                    if (stat.FloorRemoved == -1)
                    {
                        stat.FloorLeftDeck = floorLeft;
                        GD.Print($"[DeckTracker] {stat.CardId} FloorLeftDeck updated to {stat.FloorLeftDeck}");
                    }
                }
                else if (copyCounts.TryGetValue(stat.CardId, out int count))
                {
                    stat.IsInDeck = true;
                    stat.CopiesInDeck = count;
                }
            }
        }
        Publish(); // Instantly update the UI, even outside of combat!
    }
    
    // UPDATED: StartCombat is now significantly cleaner
    public static void StartCombat(string combatType, int currentFloor, List<string> activeDeckIds)
    {
        // Diff the deck immediately before processing encounters
        SyncDeckState(currentFloor, activeDeckIds);

        lock (SyncRoot)
        {
            _currentCombatType = combatType;
            GD.Print($"[DeckTracker] Starting combat state: {_currentCombatType}");
            _incrementedThisCombat.Clear(); 
            _forgeHistory.Clear();
            _conquerorTracker.Clear();
            _bladeReplayModifierTracker.Clear();
            
            foreach (var stat in Totals.Values)
            {
                stat.CombatDamage = 0; // Wipe the previous combat's text
                stat.RawForgeCombat = 0;
                stat.ConnectedForgeCombat = 0;
                stat.ReceivedForgeCombat = 0;
                
                if (!stat.IsInDeck) continue; // Skip cards not in the deck
                
                // Increment the "seen" counters right at the start of the fight
                stat.EncountersSeenTotal++;
                
                if (combatType == "Elite") stat.EncountersSeenElite++;
                else if (combatType == "Boss") stat.EncountersSeenBoss++;
                else stat.EncountersSeenHallway++;

                _incrementedThisCombat.Add(stat.CardId);
            }
        }
    }
    
    public static void ProcessCombatEnd()
    {
        lock (SyncRoot)
        {
            _currentCombatType = "Unknown"; // Clear the state
            _forgeHistory.Clear();
        }
        
        SaveState(); // Lock the victory into the hard drive
        Publish();
    }

    // --- Data Modifiers ---
    
    public static void HandleRemove(CardModel card, int floorRemoved)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                if (stat.CopiesInDeck > 1)
                {
                    stat.CopiesInDeck--;
                }
                else
                {
                    stat.FloorRemoved = floorRemoved;
                    stat.FloorLeftDeck = floorRemoved;
                    stat.IsInDeck = false;
                    stat.CopiesInDeck = 0;
                }
            }
        }
        Publish();
    }
    
    public static void RegisterCard(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
            
        lock (SyncRoot)
        {
            // card.DeckVersion will be null for the master deck cards, so check FloorAddedToDeck for
            // whether the card is generated
            bool isGenerated = card.FloorAddedToDeck == null;
            if (!Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                CardModel sourceCard = card.DeckVersion ?? card;
                string displayName = sourceCard.Title ?? sourceCard.Id.Entry ?? "Unknown";
                string enchantName = sourceCard.Enchantment?.Id.Entry ?? "";
                
                stat = new CardStats 
                { 
                    CardId = uniqueTrackingId, 
                    DisplayName = displayName,
                    CardType = sourceCard.Type.ToString(),
                    Enchantment = enchantName,
                    FloorAdded = sourceCard.FloorAddedToDeck ?? 0,
                    FloorRemoved = isGenerated ? 0 : -1, 
                    IsInDeck = !isGenerated, // Normal cards are True, Generated are False
                    CopiesInDeck = isGenerated ? 0 : 1,
                    CombatDamage = 0,
                    RunDamage = 0
                };
                
                Totals[uniqueTrackingId] = stat;
            }
            
            if (_currentCombatType != "Unknown" && isGenerated)
            {
                // HashSet.Add returns 'true' ONLY if the item wasn't already in the list.
                // This ensures 10 Shivs generated in one fight only increment the denominator by 1.
                if (_incrementedThisCombat.Add(uniqueTrackingId))
                {
                    stat.EncountersSeenTotal++;
                    if (_currentCombatType == "Elite") stat.EncountersSeenElite++;
                    else if (_currentCombatType == "Boss") stat.EncountersSeenBoss++;
                    else stat.EncountersSeenHallway++;
                }
            }
        }
    }
    
    // NEW: Draw Incrementer
    public static void AddDraw(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.TimesDrawn++;
            }
        }
        Publish(); // Instantly update UI when drawn
    }

    public static void UpdateSeekingEdge(CardModel seekingEdgeCard)
    {
        _activeSeekingEdgeCard ??= seekingEdgeCard;
        GD.Print($"[DeckTracker] Updated active seeking edge: {GetTrackingId(_activeSeekingEdgeCard)}.");
    }
    
    public static void UpdateConquerorTracker(Creature target, decimal powerChangeAmount, CardModel? cardSource)
    {
        if (!_conquerorTracker.ContainsKey(target)) _conquerorTracker[target] = new Queue<string>();
        var conquerorQueue = _conquerorTracker[target];
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
    
    // For SwordSage, replayCountAdded would be 1. Later when supporting Hidden Gem hitting this, it may be 2 or 3
    public static void UpdateSovereignBladeReplayModifierTracker(decimal replayCountAdded, CardModel? cardSource)
    {
        if (cardSource != null && replayCountAdded > 0)
        {
            var uniqueTrackingId = GetTrackingId(cardSource);
            for (var i = 0; i < (int)replayCountAdded; i++)
            {
                GD.Print($"[DeckTracker] Adding replay modifier to history with ID {uniqueTrackingId}.");
                _bladeReplayModifierTracker.Add(cardSource);
            }
        }
        Publish();
    }
    
    private static string? GetEarliestActiveConqueror(Creature target)
    {
        return _conquerorTracker.TryGetValue(target, out var conquerorQueue) ? conquerorQueue.Peek() : null;
    }
    
    // NEW: Play Incrementer
    public static void AddPlay(CardModel card)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.TimesPlayed++;
            }
        }
        Publish(); // Instantly update UI when played
    }
    
    public static void AddForge(CardModel card, decimal amount)
    {
        string uniqueTrackingId = GetTrackingId(card);
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.RawForgeTotal += amount;
                stat.RawForgeCombat += amount;
                
                // Route into specific encounter buckets
                if (_currentCombatType == "Elite") stat.RawForgeElite += amount;
                else if (_currentCombatType == "Boss") stat.RawForgeBoss += amount;
                else if (_currentCombatType == "Hallway") stat.RawForgeHallway += amount;
                
                _forgeHistory.Add(new ForgeInstance { TrackingId = uniqueTrackingId, Amount = amount });
            }
        }
        Publish();
    }

    public static void AddSovereignBladeDamageHistoryItem(DamageHistoryItem damageHistoryItem)
    {
        _sovereignBladedamageHistory.Add(damageHistoryItem);
    }
    
    public static void AddDamage(CardModel card, decimal damage)
    {
        string uniqueTrackingId = GetTrackingId(card);
        
        lock (SyncRoot)
        {
            if (Totals.TryGetValue(uniqueTrackingId, out CardStats? stat))
            {
                stat.CombatDamage += damage;
                stat.RunDamage += damage;

                // NEW: Route the damage in real-time!
                if (_currentCombatType == "Elite") stat.DamageElite += damage;
                else if (_currentCombatType == "Boss") stat.DamageBoss += damage;
                else if (_currentCombatType == "Hallway") stat.DamageHallway += damage;
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

                    if (_currentCombatType == "Elite") stat.DamageElite += damageToAttributeToConqueror;
                    else if (_currentCombatType == "Boss") stat.DamageBoss += damageToAttributeToConqueror;
                    else if (_currentCombatType == "Hallway") stat.DamageHallway += damageToAttributeToConqueror;
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
                AddDamage(bladeCard, Math.Min(damageToAttribute, sovereignBladeBaseDamage));
                damageToAttribute -= sovereignBladeBaseDamage;

                decimal damageToDistribute = Math.Max(0, damageToAttribute);
                decimal totalDistributed = 0;

                foreach (var forgeInstance in _forgeHistory)
                {
                    if (damageToDistribute <= 0) break;

                    var amountToAttribute = Math.Min(damageToDistribute, forgeInstance.Amount);
                    var idToAttribute = forgeInstance.TrackingId;

                    if (Totals.TryGetValue(idToAttribute, out var stat))
                    {
                        GD.Print($"adding connected forge to {idToAttribute} with amount {amountToAttribute}");
                        stat.ConnectedForgeCombat += amountToAttribute;
                        stat.ConnectedForgeTotal += amountToAttribute;

                        if (_currentCombatType == "Elite") stat.ConnectedForgeElite += amountToAttribute;
                        else if (_currentCombatType == "Boss") stat.ConnectedForgeBoss += amountToAttribute;
                        else if (_currentCombatType == "Hallway") stat.ConnectedForgeHallway += amountToAttribute;
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
                        bladeStat.ReceivedForgeTotal += totalDistributed;

                        if (_currentCombatType == "Elite") bladeStat.ReceivedForgeElite += totalDistributed;
                        else if (_currentCombatType == "Boss") bladeStat.ReceivedForgeBoss += totalDistributed;
                        else if (_currentCombatType == "Hallway") bladeStat.ReceivedForgeHallway += totalDistributed;
                    }
                }
            }
        }
        Publish();
    }

    public static void ProcessSovereignBladeHistory(CardPlay cardPlay)
    {
        if (_sovereignBladedamageHistory.Count == 0) return;
        var bladeCardModel = _sovereignBladedamageHistory[0].CardModel;
        if (bladeCardModel == null)
        {
            GD.Print("[DeckTracker] Warning: bladeCardModel is null in ProcessSovereignBladeHistory");
            return;
        }
        var expectedReplayCount = bladeCardModel.BaseReplayCount;
        
        GD.Print($"[DeckTracker] Processing sovereign blade history with count: {_sovereignBladedamageHistory.Count}");
        // 0 is primary play, 1+ means replayed
        var isReplay = cardPlay.PlayIndex > 0;
        GD.Print($"[DeckTracker] isReplay: {isReplay} (PlayIndex: {cardPlay.PlayIndex})");
        
        var maxTotalDamageInstance = _sovereignBladedamageHistory[0];
        foreach (var damageHistoryItem in _sovereignBladedamageHistory)
        {
            if (damageHistoryItem.Results.TotalDamage > maxTotalDamageInstance.Results.TotalDamage)
            {
                maxTotalDamageInstance = damageHistoryItem;
            }
        }

        foreach (var damageHistoryItem in _sovereignBladedamageHistory)
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
                if (trackerIndex < _bladeReplayModifierTracker.Count)
                {
                    replayModifyingCard = _bladeReplayModifierTracker[trackerIndex];
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
        _sovereignBladedamageHistory.Clear();
    }
    
    public static void ForcePublish() => Publish();

    private static void Publish()
    {
        List<CardStats> statsCopy;
        lock (SyncRoot)
        {
            statsCopy = Totals.Values.Select(s => s.Clone()).ToList();
        }
        Changed?.Invoke(statsCopy);
    }
}

public sealed class CardStats
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int FloorAdded { get; set; }
    public int FloorRemoved { get; set; } = -1;
    public int FloorLeftDeck { get; set; } = -1;
    public bool IsInDeck { get; set; } = true;
    public int CopiesInDeck { get; set; } = 0;
    
    public int TimesDrawn { get; set; }
    public int TimesPlayed { get; set; }
    public decimal PlayRate => TimesDrawn > 0 ? (decimal)TimesPlayed / TimesDrawn : 0;
    
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }
    public decimal DamageHallway { get; set; }
    public decimal DamageElite { get; set; }
    public decimal DamageBoss { get; set; }
    
    public decimal RawForgeTotal { get; set; }
    public decimal RawForgeCombat { get; set; }
    public decimal RawForgeHallway { get; set; }
    public decimal RawForgeElite { get; set; }
    public decimal RawForgeBoss { get; set; }
    public decimal ConnectedForgeCombat { get; set; }
    public decimal ConnectedForgeTotal { get; set; }
    public decimal ConnectedForgeHallway { get; set; }
    public decimal ConnectedForgeElite { get; set; }
    public decimal ConnectedForgeBoss { get; set; }
    
    public decimal ReceivedForgeCombat { get; set; }
    public decimal ReceivedForgeTotal { get; set; }
    public decimal ReceivedForgeHallway { get; set; }
    public decimal ReceivedForgeElite { get; set; }
    public decimal ReceivedForgeBoss { get; set; }

    public int EncountersSeenTotal { get; set; }
    public int EncountersSeenHallway { get; set; }
    public int EncountersSeenElite { get; set; }
    public int EncountersSeenBoss { get; set; }

    public decimal AvgTotal => EncountersSeenTotal > 0 ? RunDamage / EncountersSeenTotal : 0;
    public decimal AvgHallway => EncountersSeenHallway > 0 ? DamageHallway / EncountersSeenHallway : 0;
    public decimal AvgElite => EncountersSeenElite > 0 ? DamageElite / EncountersSeenElite : 0;
    public decimal AvgBoss => EncountersSeenBoss > 0 ? DamageBoss / EncountersSeenBoss : 0;

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId, DisplayName = DisplayName, CardType = CardType, FloorAdded = FloorAdded, 
            Enchantment = Enchantment,
            FloorRemoved = FloorRemoved, FloorLeftDeck = FloorLeftDeck, IsInDeck = IsInDeck, CopiesInDeck = CopiesInDeck,
            TimesDrawn = TimesDrawn, TimesPlayed = TimesPlayed, // Add clones here!
            CombatDamage = CombatDamage, RunDamage = RunDamage,
            DamageHallway = DamageHallway, DamageElite = DamageElite, DamageBoss = DamageBoss,
            RawForgeTotal = RawForgeTotal, RawForgeCombat = RawForgeCombat, RawForgeHallway = RawForgeHallway,
            RawForgeElite = RawForgeElite, RawForgeBoss = RawForgeBoss,
            ConnectedForgeCombat = ConnectedForgeCombat, ConnectedForgeTotal = ConnectedForgeTotal,
            ConnectedForgeHallway = ConnectedForgeHallway, ConnectedForgeElite = ConnectedForgeElite, ConnectedForgeBoss = ConnectedForgeBoss,
            ReceivedForgeCombat = ReceivedForgeCombat, ReceivedForgeTotal = ReceivedForgeTotal,
            ReceivedForgeHallway = ReceivedForgeHallway, ReceivedForgeElite = ReceivedForgeElite, ReceivedForgeBoss = ReceivedForgeBoss,
            EncountersSeenTotal = EncountersSeenTotal, EncountersSeenHallway = EncountersSeenHallway,
            EncountersSeenElite = EncountersSeenElite, EncountersSeenBoss = EncountersSeenBoss
        };
    }
}

// --- JSON Serialization Models ---
public sealed class SavedRunState
{
    public string RunSeed { get; set; } = "";
    public Dictionary<string, CardStats> Totals { get; set; } = new();
    
    // We leave this empty dictionary here so older save files don't crash when deserializing!
    public Dictionary<string, int> TypeCounters { get; set; } = new(); 
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SavedRunState))]
internal partial class SavedStateCtx : System.Text.Json.Serialization.JsonSerializerContext { }