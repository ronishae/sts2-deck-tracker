using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using System;
using System.Collections.Generic;
using System.Threading;
using MegaCrit.Sts2.Core.Entities.Creatures;
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
        public Creature? Target { get; set; }
        public Creature? Dealer { get; set; }
        public decimal BaseDamage { get; set; }
        public List<DamageModifierSnapshot> AdditiveModifiers { get; set; } = new();
        public List<DamageModifierSnapshot> MultiplicativeModifiers { get; set; } = new();
    }

    // --- STATE VARIABLES ---
    // Maps a specific Creature to their active debuffs (like Vulnerable)
    public static readonly Dictionary<Creature, Dictionary<string, List<BuffContribution>>> EnemyDebuffLedgers = new();
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
            EnemyDebuffLedgers.Clear();
        }
    }

    // --- ENEMY DEBUFF LEDGERS ---

    // --- DURATION LEDGERS (For Vulnerable, Double Damage, Weak, Intangible) ---
    // Maps a Creature (Player or Enemy) to their active turn-based buffs!
    public static readonly Dictionary<Creature, Dictionary<string, List<BuffContribution>>> DurationLedgers = new();

    public static void AddDurationBuff(Creature target, string buffType, decimal amount, string trackingId)
    {
        if (target == null || amount <= 0) return;
        lock (SyncRoot)
        {
            if (!DurationLedgers.ContainsKey(target)) DurationLedgers[target] = new();
            if (!DurationLedgers[target].ContainsKey(buffType)) DurationLedgers[target][buffType] = new List<BuffContribution>();
            
            DurationLedgers[target][buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] Added {amount} {buffType} to Duration FIFO ledger for {trackingId} on {target.Name}");
        }
    }

    public static void RemoveDurationBuff(Creature target, string buffType, decimal amount)
    {
        if (target == null || amount <= 0) return;
        lock (SyncRoot)
        {
            if (!DurationLedgers.TryGetValue(target, out var targetLedger)) return;
            if (!targetLedger.TryGetValue(buffType, out var ledger)) return;

            decimal remainingToRemove = amount;
            // FIFO removal because older durations tick down first!
            for (int i = 0; i < ledger.Count; i++)
            {
                if (remainingToRemove <= 0) break;
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
            }
            ledger.RemoveAll(c => c.Amount <= 0);
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
    
    // Helper to add persistent buffs when we already know the exact ID
    public static void AddPersistentBuffById(string buffType, decimal amount, string trackingId)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!PersistentLedgers.ContainsKey(buffType)) PersistentLedgers[buffType] = new List<BuffContribution>();
            PersistentLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] Handoff: Added {amount} {buffType} to Persistent ledger for {trackingId}");
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
        decimal basePlusAdditives = snapshot.BaseDamage;
        foreach (var a in snapshot.AdditiveModifiers) basePlusAdditives += a.Amount;

        decimal totalMultipliers = 1m;
        foreach (var m in snapshot.MultiplicativeModifiers) totalMultipliers *= m.Amount;

        decimal currentCalculatedDamage = Math.Max(0, Math.Floor(basePlusAdditives * totalMultipliers));

        decimal overkill = Math.Max(0, currentCalculatedDamage - actualDealtDamage);
        decimal extraDamage = Math.Max(0, actualDealtDamage - currentCalculatedDamage);

        // NEW: A rolling multiplier pool. Debuffs will permanently stay in this pool!
        decimal activeMultipliers = totalMultipliers;

        // --- MULTIPLIER PEEL (First) ---
        for (int i = snapshot.MultiplicativeModifiers.Count - 1; i >= 0; i--)
        {
            var multMod = snapshot.MultiplicativeModifiers[i];

            decimal multsWithout = activeMultipliers / multMod.Amount;
            decimal damageWithout = Math.Max(0, Math.Floor(basePlusAdditives * multsWithout));
            
            decimal theoreticalDiff = currentCalculatedDamage - damageWithout;

            // Buffs (>1) have a positive diff. Debuffs (<1) have a negative diff.
            if (theoreticalDiff > 0)
            {
                decimal penalty = Math.Min(theoreticalDiff, overkill);
                decimal awardedDamage = theoreticalDiff - penalty;
                overkill -= penalty;

                if (awardedDamage > 0) PayoutMultiplierDamage(multMod.PowerId, awardedDamage, snapshot.Target, snapshot.Dealer);
                
                // We successfully peeled a Buff. Update the stack!
                currentCalculatedDamage = damageWithout;
                activeMultipliers = multsWithout; 
            }
            // If Diff <= 0, it was a Debuff! We skip it. 
            // `activeMultipliers` keeps the 0.75x penalty for the next phase.
        }

        // --- ADDITIVE PEEL (Second) ---
        for (int i = snapshot.AdditiveModifiers.Count - 1; i >= 0; i--)
        {
            var addMod = snapshot.AdditiveModifiers[i];

            decimal damageWithout = snapshot.BaseDamage;
            for (int j = 0; j < i; j++) damageWithout += snapshot.AdditiveModifiers[j].Amount;
            
            // CRITICAL FIX: Multiply by the activeMultipliers (which contains the Debuffs!)
            damageWithout = Math.Max(0, Math.Floor(damageWithout * activeMultipliers));

            decimal theoreticalDiff = currentCalculatedDamage - damageWithout;

            decimal penalty = Math.Min(theoreticalDiff, overkill);
            decimal awardedDamage = theoreticalDiff - penalty;
            overkill -= penalty;

            if (awardedDamage > 0) PayoutAdditiveDamage(addMod.PowerId, awardedDamage);

            currentCalculatedDamage = damageWithout;
        }

        return Math.Max(0, currentCalculatedDamage - overkill + extraDamage);
    }
    
    private static void PayoutMultiplierDamage(string powerId, decimal amount, Creature? target, Creature? dealer)
    {
        // 1. Is it a Target Debuff? (Vulnerable)
        if (target != null && DurationLedgers.TryGetValue(target, out var targetLedger) 
                           && targetLedger.TryGetValue(powerId, out var enemyLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in enemyLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = remainingToPay; // 100% to the active turn!
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
            }
            return;
        }

        // 2. Is it a Dealer Duration Buff? (Double Damage)
        if (dealer != null && DurationLedgers.TryGetValue(dealer, out var dealerLedger) 
                           && dealerLedger.TryGetValue(powerId, out var playerDurationLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in playerDurationLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = remainingToPay; // 100% to the active turn!
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
            }
            return;
        }

        // 3. Is it a Consumable Player Buff? (e.g., Pen Nib)
        if (ConsumableLedgers.TryGetValue(powerId, out var consumableLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in consumableLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
            }
            return;
        }
        
        // 4. Is it a Persistent Player Buff? (e.g., A passive Stance or Relic modifier)
        if (PersistentLedgers.TryGetValue(powerId, out var persistentLedger))
        {
            decimal totalPool = 0;
            foreach (var c in persistentLedger) totalPool += c.Amount;
        
            if (totalPool > 0)
            {
                decimal remainingToPay = amount;
            
                for (int i = 0; i < persistentLedger.Count; i++)
                {
                    var contribution = persistentLedger[i];
                    if (remainingToPay <= 0) break;

                    // If this is the final card in the queue, it skips the math and catches the remainder.
                    // This guarantees ZERO damage is lost to floating-point truncation!
                    if (i == persistentLedger.Count - 1)
                    {
                        AddDamageById(contribution.TrackingId, remainingToPay);
                        break;
                    }

                    // Older cards (FIFO) get the rounding priority!
                    decimal share = Math.Min(remainingToPay, Math.Ceiling(amount * (contribution.Amount / totalPool)));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        remainingToPay -= share;
                    }
                }
            }
        }
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