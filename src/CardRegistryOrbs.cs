using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeckTracker;

public static partial class CardRegistry
{
    // --- CONTEXT CLASSES ---
    public class FocusContribution
    {
        public string TrackingId { get; set; } = "";
        public decimal Amount { get; set; }
        public bool IsTemporary { get; set; } 
    }

    public class OrbExecutionContext
    {
        public OrbModel Orb { get; }
        public bool IsEvoke { get; }
        public decimal ExpectedValue { get; }

        public OrbExecutionContext(OrbModel orb, bool isEvoke, decimal expectedValue)
        {
            Orb = orb;
            IsEvoke = isEvoke;
            ExpectedValue = expectedValue;
        }
    }

    // --- STATE VARIABLES ---
    
    public static readonly AsyncLocal<bool> IsApplyingTemporaryFocus = new();
    public static readonly AsyncLocal<bool> IsExpiringTemporaryFocus = new();
    
    private static readonly Dictionary<OrbModel, string> OrbChannelers = new();
    private static readonly List<FocusContribution> FocusHistory = new();
    
    // Tracks the card currently being played to tag the orb during Channel()
    public static readonly AsyncLocal<CardModel?> CurrentPlayingCard = new();
    
    // Tracks if an Orb is currently dealing damage
    public static readonly AsyncLocal<OrbExecutionContext?> ExecutingOrb = new();
    
    public static readonly List<string> UnattributedOrbLogs = new();

    public static void ResetOrbState()
    {
        lock (SyncRoot)
        {
            OrbChannelers.Clear();
            FocusHistory.Clear();
            UnattributedOrbLogs.Clear();
        }
    }

    // --- CHANNELING IDENTITY ---
    
    public static void RegisterChanneledOrb(OrbModel orb, CardModel? sourceCard)
    {
        lock (SyncRoot)
        {
            // If there is no card, it was channeled by a Relic, a Potion, or an Event
            string trackingId = sourceCard != null ? GetTrackingId(sourceCard) : "External_Relic";
            
            OrbChannelers[orb] = trackingId;
            GD.Print($"[DeckTracker] Channeled {orb.Id.Entry} and attributed to {trackingId}");
        }
    }

    public static void DeregisterOrb(OrbModel orb)
    {
        lock (SyncRoot)
        {
            if (OrbChannelers.ContainsKey(orb))
            {
                OrbChannelers.Remove(orb);
            }
        }
    }

    // --- FOCUS LEDGER ---
    
    public static void LogFocusChange(CardModel? cardSource, decimal amount)
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

            // 2. STANDARD APPLICATION: (Cards, Relics, or Enemy Debuffs)
            
            // If no card source, it's either an organic buff or an enemy debuff
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : (amount > 0 ? "External_Buff" : "External_Debuff");
            
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

        lock (SyncRoot)
        {
            OrbModel orb = context.Orb;
            
            if (!OrbChannelers.TryGetValue(orb, out string channelerId))
            {
                // --- THE GENERIC MULTI-EVOKE FALLBACK ---
                // If the orb isn't in the ledger, it has already been evoked once and deregistered.
                // If a card is currently playing, it forced this phantom evoke and gets 100% of the credit!
                if (context.IsEvoke && CurrentPlayingCard.Value != null)
                {
                    string multiEvokerId = GetTrackingId(CurrentPlayingCard.Value);
                    AddDamageById(multiEvokerId, totalDamage);
                    GD.Print($"[DeckTracker] Phantom Evoke! Paid {totalDamage:F2} Bonus Evoke Damage to {multiEvokerId}");
                    return;
                }
                
                // If no card is playing, it's a true unaccounted error.
                UnattributedOrbLogs.Add($"Unattributed {orb.Id.Entry} Orb Damage ({totalDamage}). Cause: No channeling card found.");
                return;
            }

            decimal currentEngineOrbValue = context.ExpectedValue;
            decimal totalFocus = player.GetPower<FocusPower>()?.Amount ?? 0m;
            decimal pureBase = currentEngineOrbValue - totalFocus;

            decimal channelerPayout;
            decimal focusPayoutTotal;

            if (totalFocus >= 0)
            {
                // Standard Waterfall
                channelerPayout = Math.Min(totalDamage, pureBase);
                focusPayoutTotal = totalDamage - channelerPayout;
            }
            else
            {
                // Negative Focus Waterfall (Damage Debt)
                decimal theoreticalDamage = totalDamage - totalFocus; 
                channelerPayout = Math.Min(theoreticalDamage, pureBase);
                focusPayoutTotal = totalDamage - channelerPayout; 
            }

            // 1. Payout the Channeler
            if (channelerPayout != 0)
            {
                AddDamageById(channelerId, channelerPayout);
                GD.Print($"[DeckTracker] Waterfall Paid {channelerPayout:F2} Base Orb Damage to {channelerId}");
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
        try
        {
            await originalTask;
        }
        finally
        {
            ExecutingOrb.Value = null;
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
            ExecutingOrb.Value = null;
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
}