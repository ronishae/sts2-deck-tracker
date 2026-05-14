using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using System;
using System.Collections.Generic;
using System.Threading;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // --- CONTEXT CLASSES ---
    public class BuffContribution
    {
        public string TrackingId { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class DamageModifierSnapshot
    {
        public string PowerId { get; set; } = "";
        public decimal Amount { get; set; }
    }

    public class DamageSnapshot
    {
        public CardModel? CardSource { get; set; }
        public decimal BaseDamage { get; set; }
        public List<DamageModifierSnapshot> AdditiveModifiers { get; set; } = new();
        public List<DamageModifierSnapshot> MultiplicativeModifiers { get; set; } = new();
    }

    // --- STATE VARIABLES ---
    public static readonly Dictionary<string, List<BuffContribution>> PersistentLedgers = new();
    public static readonly Dictionary<string, List<BuffContribution>> ConsumableLedgers = new();
    
    // The Snapshot trap we will use in Phase 3
    public static readonly AsyncLocal<DamageSnapshot?> CurrentAttackSnapshot = new();

    public static void ResetBuffState()
    {
        lock (SyncRoot)
        {
            PersistentLedgers.Clear();
            ConsumableLedgers.Clear();
        }
    }

    // --- BUFF LEDGER LOGIC ---
    
    // For Strength, Accuracy, Phantom Blades
    public static void AddPersistentBuff(string buffType, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!PersistentLedgers.ContainsKey(buffType)) PersistentLedgers[buffType] = new List<BuffContribution>();
            
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            PersistentLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} {buffType} to persistent ledger for {trackingId}");
        }
    }

    public static void RemovePersistentBuff(string buffType, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!PersistentLedgers.TryGetValue(buffType, out var ledger)) return;
            
            decimal remainingToRemove = amount;
            // Remove LIFO (Last-In-First-Out) for things like Temporary Strength expiring or Debuffs
            for (int i = ledger.Count - 1; i >= 0; i--)
            {
                if (remainingToRemove <= 0) break;
                
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
            }
            ledger.RemoveAll(c => c.Amount <= 0);
            GD.Print($"[DeckTracker] Removed {amount} {buffType} from persistent ledger.");
        }
    }

    // For Vigor, Pen Nib
    public static void AddConsumableBuff(string buffType, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.ContainsKey(buffType)) ConsumableLedgers[buffType] = new List<BuffContribution>();
            
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            ConsumableLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} {buffType} to consumable FIFO ledger for {trackingId}");
        }
    }

    public static void RemoveConsumableBuff(string buffType, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.TryGetValue(buffType, out var ledger)) return;

            decimal remainingToRemove = amount;
            // Remove FIFO (First-In-First-Out) because older Vigor gets consumed first!
            for (int i = 0; i < ledger.Count; i++)
            {
                if (remainingToRemove <= 0) break;
                
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
            }
            ledger.RemoveAll(c => c.Amount <= 0);
            GD.Print($"[DeckTracker] Consumed {amount} {buffType} from FIFO ledger.");
        }
    }
    
    public static decimal ProcessDamageSnapshot(DamageSnapshot snapshot, decimal actualDealtDamage)
    {
        decimal totalMultipliers = 1m;
        foreach (var m in snapshot.MultiplicativeModifiers) totalMultipliers *= m.Amount;

        // Reconstruct the highest theoretical damage BEFORE truncation
        decimal currentCalculatedDamage = snapshot.BaseDamage;
        foreach (var a in snapshot.AdditiveModifiers) currentCalculatedDamage += a.Amount;
        currentCalculatedDamage = Math.Max(0, Math.Floor(currentCalculatedDamage * totalMultipliers));

        // Calculate the exact overkill amount. 
        // We also calculate 'extraDamage' just in case a future mod creates a scenario where Actual > Calculated.
        decimal overkill = Math.Max(0, currentCalculatedDamage - actualDealtDamage);
        decimal extraDamage = Math.Max(0, actualDealtDamage - currentCalculatedDamage);

        // LIFO (Last-In-First-Out) Peel
        for (int i = snapshot.AdditiveModifiers.Count - 1; i >= 0; i--)
        {
            var addMod = snapshot.AdditiveModifiers[i];

            // Calculate damage WITHOUT this specific modifier
            decimal damageWithout = snapshot.BaseDamage;
            for (int j = 0; j < i; j++) damageWithout += snapshot.AdditiveModifiers[j].Amount;
            damageWithout = Math.Max(0, Math.Floor(damageWithout * totalMultipliers));

            // The isolated integer damage this buff provided theoretically
            decimal theoreticalDiff = currentCalculatedDamage - damageWithout;

            // The newest buffs eat the overkill penalty first!
            decimal penalty = Math.Min(theoreticalDiff, overkill);
            decimal awardedDamage = theoreticalDiff - penalty;
            
            // Deduct the penalty from the running overkill total
            overkill -= penalty;

            if (awardedDamage > 0)
            {
                PayoutAdditiveDamage(addMod.PowerId, awardedDamage);
            }

            currentCalculatedDamage = damageWithout;
        }

        // Return the remaining damage to the base Card!
        // currentCalculatedDamage is now just the pure Base Damage (with multipliers).
        // We subtract any remaining overkill (if the base damage itself overkilled the enemy)
        // and add any extra damage (if Actual > Calc).
        return Math.Max(0, currentCalculatedDamage - overkill + extraDamage);
    }
    
    private static void PayoutAdditiveDamage(string powerId, decimal amount)
    {
        // 1. Is it a Consumable? (Vigor)
        if (ConsumableLedgers.ContainsKey(powerId))
        {
            // Because Vigor drops naturally, we don't physically remove it from the ledger here.
            // We just peek at the FIFO queue to see WHO gave the Vigor, and award them!
            decimal remainingToPay = amount;
            foreach (var contribution in ConsumableLedgers[powerId])
            {
                if (remainingToPay <= 0) break;
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                
                AddDamageById(contribution.TrackingId, payout);
                GD.Print($"[DeckTracker] LIFO Peel Paid {payout} Consumable Buff Damage to {contribution.TrackingId}");
                
                remainingToPay -= payout;
            }
        }
        // 2. Is it a Persistent Buff? (Strength, Accuracy, PhantomBlades)
        else if (PersistentLedgers.ContainsKey(powerId))
        {
            // Persistent buffs don't get consumed, so we just read the ledger top-down!
            foreach (var contribution in PersistentLedgers[powerId])
            {
                // To maintain accurate attribution without decimals, we give the entire Peel diff 
                // to the card that gave the largest contribution, or we can split it proportionally if you prefer. 
                // For now, we will just give it directly based on their share of the pool.
                decimal totalPool = 0;
                foreach(var c in PersistentLedgers[powerId]) totalPool += c.Amount;
                
                if (totalPool > 0)
                {
                    decimal share = Math.Floor(amount * (contribution.Amount / totalPool));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        GD.Print($"[DeckTracker] LIFO Peel Paid {share} Persistent Buff Damage to {contribution.TrackingId}");
                    }
                }
            }
        }
    }
}