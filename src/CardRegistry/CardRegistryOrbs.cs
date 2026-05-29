using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;

using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    // --- STATE VARIABLES ---
    
    public static readonly AsyncLocal<bool> IsApplyingTemporaryFocus = new();
    public static readonly AsyncLocal<bool> IsExpiringTemporaryFocus = new();
    
    private static readonly Dictionary<OrbModel, string> OrbChannelers = new();
    private static readonly List<FocusContribution> FocusHistory = new();
    
    private static readonly Dictionary<OrbModel, List<OrbContribution>> DarkOrbLedgers = new();
    
    public static readonly List<LoopContribution> LoopHistory = new();
    public static readonly List<string> CurrentTurnLoopQueue = new();
    public static readonly AsyncLocal<bool> IsLoopExecuting = new();
    
    // Tracks if an Orb is currently dealing damage
    private static readonly AsyncLocal<OrbExecutionContext?> _executingOrb = new();
    public static OrbExecutionContext? ExecutingOrb
    {
        get => _executingOrb.Value;
        set => _executingOrb.Value = value;
    }
    
    public static readonly List<string> UnattributedOrbLogs = new();

    public static void ResetOrbState()
    {
        lock (SyncRoot)
        {
            OrbChannelers.Clear();
            FocusHistory.Clear();
            UnattributedOrbLogs.Clear();
            DarkOrbLedgers.Clear();
            LoopHistory.Clear();
        }
    }

    // --- CHANNELING IDENTITY ---
    
    public static void RegisterChanneledOrb(OrbModel orb, CardModel? sourceCard)
    {
        lock (SyncRoot)
        {
            // If there is no card, it was channeled by a Relic, a Potion, or an Event
            string trackingId = sourceCard != null ? GetTrackingId(sourceCard) : 
                (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value) ? 
                    "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value : 
                    "External_Relic");
            
            if (IsStormExecuting.Value)
            {
                var stormId = GetNextStormTrackingId();
                if (stormId != null) trackingId = stormId;
            }
            
            // Check if Trash to Treasure is executing its loop!
            if (IsTrashToTreasureExecuting.Value && TrashToTreasureAttributionQueue.Value != null)
            {
                // Pop the next ID off the queue!
                if (TrashToTreasureAttributionQueue.Value.TryDequeue(out string? t2tId))
                {
                    trackingId = t2tId;
                    GD.Print($"[DeckTracker] Routed random channeled orb to Trash to Treasure source: {trackingId}");
                }
            }
            
            OrbChannelers[orb] = trackingId;
            GD.Print($"[DeckTracker] Channeled {orb.Id.Entry} and attributed to {trackingId}");
            
            if (orb is DarkOrb)
            {
                DarkOrbLedgers[orb] = new List<OrbContribution>();
                
                // Directly add the EvokeVal (which is strictly the base 6 on channel) 
                // Focus gets 0 credit for the initial channel!
                DarkOrbLedgers[orb].Add(new OrbContribution { TrackingId = trackingId, Amount = orb.EvokeVal });
                
                GD.Print($"[DeckTracker] Dark Orb Channel: Logged {orb.EvokeVal:F2} Initial Base to {trackingId}");
            }
        }
    }

    public static void DeregisterOrb(OrbModel orb)
    {
        lock (SyncRoot)
        {
            if (OrbChannelers.ContainsKey(orb))
            {
                OrbChannelers.Remove(orb);
                if (DarkOrbLedgers.ContainsKey(orb)) DarkOrbLedgers.Remove(orb);
            }
        }
    }
    
    public static void AddLoop(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;
        lock (SyncRoot)
        {
            string id = GetTrackingId(cardSource);
            LoopHistory.Add(new LoopContribution { TrackingId = id, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} Loop to ledger for {id}");
        }
    }
    
    public static void RecordDarkOrbWave(OrbModel orb, decimal waveAmount, string? forcedActorId = null)
    {
        lock (SyncRoot)
        {
            if (!DarkOrbLedgers.TryGetValue(orb, out var ledger)) return;

            // Snapshot the current focus state
            decimal totalFocus = 0;
            foreach (var focus in FocusHistory) totalFocus += focus.Amount;

            decimal pureBase = waveAmount - totalFocus;
            if (totalFocus < 0)
            {
                pureBase = waveAmount;
                totalFocus = 0;
            }

            // 1. Log the Base Actor
            if (pureBase > 0)
            {
                string? baseId = forcedActorId;
                if (baseId == null && OrbChannelers.TryGetValue(orb, out var channeler)) baseId = channeler;
                if (baseId == null) baseId = "External_Relic";

                ledger.Add(new OrbContribution { TrackingId = baseId, Amount = pureBase });
                GD.Print($"[DeckTracker] Dark Wave: Logged {pureBase:F2} Base to {baseId}");
            }
            // 2. Log the active Focus Queue (Preserves exact ordering for this wave)
            if (totalFocus > 0)
            {
                foreach (var focus in FocusHistory)
                {
                    if (focus.Amount > 0)
                    {
                        ledger.Add(new OrbContribution { TrackingId = focus.TrackingId, Amount = focus.Amount });
                        GD.Print($"[DeckTracker] Dark Orb Wave: Logged {focus.Amount:F2} Focus to {focus.TrackingId}");
                    }
                }
            }
        }
    }
    
    // --- FOCUS LEDGER ---
    
    public static void LogFocusChange(CardModel? cardSource, decimal amount)
    {
        // 2. STANDARD APPLICATION: (Cards, Relics, or Enemy Debuffs)
        string trackingId = cardSource != null ? GetTrackingId(cardSource) : 
            (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId.Value) ? 
                "RELIC_" + RelicExecutionManager.ExecutingRelicId.Value : 
                (amount > 0 ? "External_Buff" : "External_Debuff"));
        
        LogFocusChangeById(trackingId, amount);
    }

    public static void LogFocusChangeById(string trackingId, decimal amount)
    {
        if (amount == 0) return;

        lock (SyncRoot)
        {
            // 1. THE ERASURE TRAP: Is a temporary power currently falling off?
            if (IsExpiringTemporaryFocus.Value)
            {
                decimal remainingToErase = Math.Abs(amount);
                for (int i = 0; i < FocusHistory.Count; i++)
                {
                    if (remainingToErase <= 0) break;
                    
                    var contribution = FocusHistory[i];
                    
                    // CRITICAL: Only erase from contributions explicitly marked as temporary!
                    if (contribution.Amount > 0 && contribution.IsTemporary)
                    {
                        decimal erased = Math.Min(remainingToErase, contribution.Amount);
                        contribution.Amount -= erased;
                        remainingToErase -= erased;
                    }
                }
                FocusHistory.RemoveAll(c => c.Amount == 0);
                GD.Print("[DeckTracker] Erased expired temporary focus from the ledger.");
                return;
            }
            
            // Check the Apply trap
            bool isTemp = IsApplyingTemporaryFocus.Value;
            
            FocusHistory.Add(new FocusContribution { 
                TrackingId = trackingId, 
                Amount = amount, 
                IsTemporary = isTemp 
            });
            
            GD.Print($"[DeckTracker] Logged {amount} Focus for {trackingId} (Temporary: {isTemp})");
        }
    }
    
    // --- DAMAGE DISTRIBUTION (WATERFALL) ---
    
    public static void DistributeOrbDamage(OrbExecutionContext context, decimal totalDamage, Creature player)
    {
        if (totalDamage <= 0) return;

        if (IsThunderExecuting)
        {
            DistributeThunderDamage(totalDamage);
            return;
        }

        lock (SyncRoot)
        {
            decimal remainingDamageToDistributeModifyerPeel = totalDamage;
            decimal totalRelicModifiers = 0m; // NEW: Track the total peeled modifiers!

            // 1. PEEL ORB MODIFIER RELICS (e.g., Infused Core's +1 Lightning)
            if (RelicExecutionManager.PendingOrbModifiers.Value != null)
            {
                foreach (var mod in RelicExecutionManager.PendingOrbModifiers.Value)
                {
                    decimal payout = Math.Min(remainingDamageToDistributeModifyerPeel, mod.delta);
                    AddRelicDamage(mod.relicId, payout);
                    remainingDamageToDistributeModifyerPeel -= payout;
                    totalRelicModifiers += mod.delta; // Track what we stripped!
                }
                RelicExecutionManager.PendingOrbModifiers.Value.Clear();
            }
            
            OrbModel orb = context.Orb;
            
            if (!OrbChannelers.TryGetValue(orb, out string? channelerId))
            {
                // --- THE GENERIC MULTI-EVOKE FALLBACK ---
                // If the orb isn't in the ledger, it has already been evoked once and deregistered.
                // If a card is currently playing, it forced this phantom evoke and gets 100% of the credit!
                if (context.IsEvoke && CurrentPlayingCard != null)
                {
                    string multiEvokerId = GetTrackingId(CurrentPlayingCard);
                    AddDamageById(multiEvokerId, remainingDamageToDistributeModifyerPeel);
                    GD.Print($"[DeckTracker] Phantom Evoke! Paid {remainingDamageToDistributeModifyerPeel:F2} Bonus Evoke Damage to {multiEvokerId}");
                    return;
                }
                
                // If no card is playing, it's a true unaccounted error.
                UnattributedOrbLogs.Add($"Unattributed {orb.Id.Entry} Orb Damage ({remainingDamageToDistributeModifyerPeel}). Cause: No channeling card found.");
                return;
            }
            
            if (orb is DarkOrb)
            {
                if (DarkOrbLedgers.TryGetValue(orb, out var ledger))
                {
                    decimal remainingDamageToDistribute = remainingDamageToDistributeModifyerPeel;
                    
                    // Walk through the concatenated waves in pure FIFO order
                    foreach (var contribution in ledger)
                    {
                        if (remainingDamageToDistribute <= 0) break;
                        
                        decimal payout = Math.Min(remainingDamageToDistribute, contribution.Amount);
                        AddDamageById(contribution.TrackingId, payout);
                        remainingDamageToDistribute -= payout;
                        
                        GD.Print($"[DeckTracker] Dark Orb FIFO: Paid {payout:F2} to {contribution.TrackingId}");
                    }

                    if (remainingDamageToDistribute > 0)
                    {
                        GD.Print($"[DeckTracker] {remainingDamageToDistribute} Dark Orb Damage unaccounted for in ledger.");
                    }
                }
                return; // Dark Orb processed. Exit before running the Lightning/Glass waterfall!
            }
            
            decimal currentEngineOrbValue = context.ExpectedValue;
            decimal totalFocus = player.GetPower<FocusPower>()?.Amount ?? 0m;
            
            // CRITICAL FIX: Subtract the relic modifiers from the pure base!
            decimal pureBase = currentEngineOrbValue - totalFocus - totalRelicModifiers;

            decimal channelerPayout;
            decimal focusPayoutTotal;

            if (totalFocus >= 0)
            {
                // Standard Waterfall
                channelerPayout = Math.Min(remainingDamageToDistributeModifyerPeel, pureBase);
                focusPayoutTotal = remainingDamageToDistributeModifyerPeel - channelerPayout;
            }
            else
            {
                // Negative Focus Waterfall (Damage Debt)
                decimal theoreticalDamage = remainingDamageToDistributeModifyerPeel - totalFocus; 
                channelerPayout = Math.Min(theoreticalDamage, pureBase);
                focusPayoutTotal = remainingDamageToDistributeModifyerPeel - channelerPayout; 
            }

            // 1. Payout the Channeler
            if (channelerPayout != 0)
            {
                if (!context.IsEvoke && context.ForcedActorId != null)
                {
                    // A Loop or Darkness card forced this passive trigger!
                    AddDamageById(context.ForcedActorId, channelerPayout);
                    GD.Print($"[DeckTracker] Waterfall Paid {channelerPayout:F2} Base to Forcing Actor ({context.ForcedActorId})");
                }
                else
                {
                    // Standard Natural Passive or Evoke
                    AddDamageById(channelerId, channelerPayout);
                    GD.Print($"[DeckTracker] Waterfall Paid {channelerPayout:F2} Base Orb Damage to {channelerId}");
                }
            }
            
            // 2. Payout the Focus Queue (Positive Damage or Negative Debt)
            if (focusPayoutTotal != 0)
            {
                foreach (var contribution in FocusHistory)
                {
                    if (focusPayoutTotal == 0) break;

                    if (focusPayoutTotal > 0 && contribution.Amount > 0)
                    {
                        decimal payout = Math.Min(focusPayoutTotal, contribution.Amount);
                        AddDamageById(contribution.TrackingId, payout);
                        focusPayoutTotal -= payout;
                        GD.Print($"[DeckTracker] Waterfall Paid {payout:F2} Focus Damage to {contribution.TrackingId}");
                    }
                    else if (focusPayoutTotal < 0 && contribution.Amount < 0)
                    {
                        decimal debtPayout = Math.Max(focusPayoutTotal, contribution.Amount); 
                        AddDamageById(contribution.TrackingId, debtPayout);
                        focusPayoutTotal -= debtPayout;
                        GD.Print($"[DeckTracker] Waterfall Assigned {debtPayout:F2} Damage Debt to {contribution.TrackingId}");
                    }
                }
            }

            if (focusPayoutTotal > 0)
            {
                GD.Print($"[DeckTracker] {focusPayoutTotal} Orb Damage unaccounted for by Base/Focus (Likely Relic modification).");
            }
        }
        Publish();
    }
    
    // --- ASYNC WRAPPERS ---
    
    public static async Task AwaitOrbExecutionTaskAsync(Task originalTask, OrbModel orb, bool isEvoke)
    {
        var context = ExecutingOrb;
        try
        {
            await originalTask;
            if (!isEvoke && orb is DarkOrb && context != null)
            {
                GD.Print($"[DeckTracker] {orb.GetType().Name} Orb Execution Task");
                // We use the ExpectedValue (PassiveVal) we cached in the Prefix
                RecordDarkOrbWave(orb, context.ExpectedValue, context.ForcedActorId);
            }
        }
        finally
        {
            ExecutingOrb = null;
            if (isEvoke) DeregisterOrb(orb);
        }
    }
    
    public static async Task<T> AwaitOrbEvokeTaskAsync<T>(Task<T> originalTask, OrbModel orb)
    {
        try
        {
            return await originalTask;
        }
        finally
        {
            ExecutingOrb = null;
            DeregisterOrb(orb);
        }
    }
    
    public static async Task AwaitTempFocusApplyAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsApplyingTemporaryFocus.Value = false; }
    }

    public static async Task AwaitTempFocusExpireAsync(Task originalTask)
    {
        try { await originalTask; }
        finally { IsExpiringTemporaryFocus.Value = false; }
    }
    
    public static async Task AwaitLoopTaskAsync(Task originalTask)
    {
        try 
        { 
            await originalTask; 
        }
        finally 
        { 
            IsLoopExecuting.Value = false; 
            CurrentTurnLoopQueue.Clear();
        }
    }
}