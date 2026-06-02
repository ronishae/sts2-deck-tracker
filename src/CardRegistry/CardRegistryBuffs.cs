using Godot;
using MegaCrit.Sts2.Core.Entities.Cards;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

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
        public PowerModel? PowerInstance { get; set; }
    }

    public class DamageSnapshot
    {
        public CardModel? CardSource { get; set; }
        public Creature? Target { get; set; }
        public Creature? Dealer { get; set; }
        public decimal BaseDamage { get; set; }
        public MegaCrit.Sts2.Core.ValueProps.ValueProp Props { get; set; }
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
            GD.Print("[DeckTracker] ResetBuffState. All buff ledgers cleared.");
        }
    }

    public static void AddConsumableBuffById(string buffType, decimal amount, string trackingId)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.ContainsKey(buffType))
            {
                ConsumableLedgers[buffType] = new List<BuffContribution>();
            }
            ConsumableLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] AddConsumableBuffById. Added {amount} {buffType} to Consumable FIFO ledger for {trackingId}");
        }
    }

    // --- ENEMY DEBUFF LEDGERS ---

    // --- DURATION LEDGERS (For Vulnerable, Double Damage, Weak, Intangible) ---
    // Maps a Creature (Player or Enemy) to their active turn-based buffs!
    public static readonly Dictionary<Creature, Dictionary<string, List<BuffContribution>>> DurationLedgers = new();

    public static void AddDurationBuff(Creature target, string buffType, decimal amount, string trackingId)
    {
        if (target == null || amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!DurationLedgers.ContainsKey(target))
            {
                DurationLedgers[target] = new();
            }
            if (!DurationLedgers[target].ContainsKey(buffType))
            {
                DurationLedgers[target][buffType] = new List<BuffContribution>();
            }
            
            DurationLedgers[target][buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] AddDurationBuff. Added {amount} {buffType} to Duration FIFO ledger for {trackingId} on {target.Name}");
        }
    }

    public static void RemoveDurationBuff(Creature target, string buffType, decimal amount)
    {
        if (target == null || amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!DurationLedgers.TryGetValue(target, out var targetLedger))
            {
                return;
            }
            if (!targetLedger.TryGetValue(buffType, out var ledger))
            {
                return;
            }

            decimal remainingToRemove = amount;
            GD.Print($"[DeckTracker] RemoveDurationBuff. Removing {amount} {buffType} from {target.Name}");
            
            // FIFO removal because older durations tick down first!
            for (int i = 0; i < ledger.Count; i++)
            {
                if (remainingToRemove <= 0)
                {
                    break;
                }
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
                GD.Print($"[DeckTracker]   -> Erased {erased} from {contribution.TrackingId}");
            }
            ledger.RemoveAll(c => c.Amount <= 0);
        }
    }
    
    // --- BUFF LEDGER LOGIC ---
    
    // For Strength, Accuracy, Phantom Blades
    public static void AddPersistentBuff(string buffType, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!PersistentLedgers.ContainsKey(buffType))
            {
                PersistentLedgers[buffType] = new List<BuffContribution>();
            }
            
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            PersistentLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] AddPersistentBuff. Added {amount} {buffType} to persistent ledger for {trackingId}");
        }
    }
    
    // Helper to add persistent buffs when we already know the exact ID
    public static void AddPersistentBuffById(string buffType, decimal amount, string trackingId)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!PersistentLedgers.ContainsKey(buffType))
            {
                PersistentLedgers[buffType] = new List<BuffContribution>();
            }
            PersistentLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] AddPersistentBuffById. Added {amount} {buffType} to Persistent ledger for {trackingId}");
        }
    }
    
    public static void RemovePersistentBuff(string buffType, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!PersistentLedgers.TryGetValue(buffType, out var ledger))
            {
                return;
            }
            
            decimal remainingToRemove = amount;
            GD.Print($"[DeckTracker] RemovePersistentBuff. Removing {amount} {buffType}");
            
            // Remove LIFO (Last-In-First-Out) for things like Temporary Strength expiring or Debuffs
            for (int i = ledger.Count - 1; i >= 0; i--)
            {
                if (remainingToRemove <= 0)
                {
                    break;
                }
                
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
                GD.Print($"[DeckTracker]   -> Erased {erased} from {contribution.TrackingId}");
            }
            ledger.RemoveAll(c => c.Amount <= 0);
        }
    }

    // For Vigor, Pen Nib
    public static void AddConsumableBuff(string buffType, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.ContainsKey(buffType))
            {
                ConsumableLedgers[buffType] = new List<BuffContribution>();
            }
            
            string trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            ConsumableLedgers[buffType].Add(new BuffContribution { TrackingId = trackingId, Amount = amount });
            GD.Print($"[DeckTracker] AddConsumableBuff. Added {amount} {buffType} to consumable FIFO ledger for {trackingId}");
        }
    }

    public static void RemoveConsumableBuff(string buffType, decimal amount)
    {
        if (amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            if (!ConsumableLedgers.TryGetValue(buffType, out var ledger))
            {
                return;
            }

            decimal remainingToRemove = amount;
            GD.Print($"[DeckTracker] RemoveConsumableBuff. Consuming {amount} {buffType}");
            
            // Remove FIFO (First-In-First-Out) because older Vigor gets consumed first!
            for (int i = 0; i < ledger.Count; i++)
            {
                if (remainingToRemove <= 0)
                {
                    break;
                }
                
                var contribution = ledger[i];
                decimal erased = Math.Min(remainingToRemove, contribution.Amount);
                contribution.Amount -= erased;
                remainingToRemove -= erased;
                GD.Print($"[DeckTracker]   -> Erased {erased} from {contribution.TrackingId}");
            }
            ledger.RemoveAll(c => c.Amount <= 0);
        }
    }
    
    public static decimal ProcessDamageSnapshot(DamageSnapshot snapshot, decimal actualDealtDamage)
    {
        decimal basePlusAdditives = snapshot.BaseDamage;
        foreach (var a in snapshot.AdditiveModifiers)
        {
            basePlusAdditives += a.Amount;
        }

        decimal totalMultipliers = 1m;
        foreach (var m in snapshot.MultiplicativeModifiers)
        {
            totalMultipliers *= m.Amount;
        }

        decimal currentCalculatedDamage = Math.Max(0, Math.Floor(basePlusAdditives * totalMultipliers));

        decimal overkill = Math.Max(0, currentCalculatedDamage - actualDealtDamage);
        decimal extraDamage = Math.Max(0, actualDealtDamage - currentCalculatedDamage);

        // NEW: A rolling multiplier pool. Debuffs will permanently stay in this pool!
        decimal activeMultipliers = totalMultipliers;

        GD.Print($"[DeckTracker] ProcessDamageSnapshot. Total Calculated: {currentCalculatedDamage}, Actual: {actualDealtDamage}, Overkill: {overkill}, Extra: {extraDamage}");

        // --- MULTIPLIER PEEL (First) ---
        for (int i = snapshot.MultiplicativeModifiers.Count - 1; i >= 0; i--)
        {
            var multMod = snapshot.MultiplicativeModifiers[i];

            // NEW: Special Decomposition for Vulnerable!
            if (multMod.PowerId == "VULNERABLE_POWER")
            {
                // 1. Grab the active game objects
                var vulnPower = snapshot.Target?.GetPower<VulnerablePower>();
                var phrog = snapshot.Dealer?.Player?.GetRelic<PaperPhrog>();
                var cruelty = snapshot.Dealer?.GetPower<CrueltyPower>();
                var debilitate = snapshot.Target?.GetPower<DebilitatePower>();

                // 2. Dynamically fetch the base Vulnerable value (Usually 1.5m, but grabs the true DynamicVar!)
                decimal m_base = vulnPower != null ? vulnPower.DynamicVars["DamageIncrease"].BaseValue : 1.5m;
                
                // 3. Chain the Native Game Methods!
                decimal m_phrog = phrog != null 
                    ? phrog.ModifyVulnerableMultiplier(snapshot.Target!, m_base, snapshot.Props, snapshot.Dealer, snapshot.CardSource) 
                    : m_base;
                    
                decimal m_cruel = cruelty != null 
                    ? cruelty.ModifyVulnerableMultiplier(snapshot.Target!, m_phrog, snapshot.Props, snapshot.Dealer, snapshot.CardSource) 
                    : m_phrog;
                    
                decimal multsWithoutVuln = activeMultipliers / multMod.Amount;

                // Helper to cleanly peel a sub-layer and pass the Diff
                void PeelSubMultiplier(string id, decimal multWithout)
                {
                    decimal dmgWithout = Math.Max(0, Math.Floor(basePlusAdditives * (multsWithoutVuln * multWithout)));
                    decimal diff = currentCalculatedDamage - dmgWithout;

                    if (diff > 0)
                    {
                        decimal penalty = Math.Min(diff, overkill);
                        decimal awarded = diff - penalty;
                        overkill -= penalty;

                        if (awarded > 0)
                        {
                            bool paid = PayoutMultiplierDamage(id, awarded, snapshot.Target, snapshot.Dealer, multMod.PowerInstance);
                            if (!paid)
                            {
                                extraDamage += awarded;
                                GD.Print($"[DeckTracker] ProcessDamageSnapshot. Unattributed {awarded} damage from {id} routed to Base Card.");
                            }
                        }
                    }
                    currentCalculatedDamage = dmgWithout;
                }

                // 4. Reverse Execution Order: Debilitate -> Cruelty -> Phrog -> Base
                if (debilitate != null)
                {
                    PeelSubMultiplier("DEBILITATE_POWER", m_cruel);
                }
                if (cruelty != null)
                {
                    PeelSubMultiplier("CRUELTY_POWER", m_phrog);
                }
                if (phrog != null)
                {
                    PeelSubMultiplier("PAPER_PHROG", m_base);
                }
                PeelSubMultiplier("VULNERABLE_POWER", 1m);

                activeMultipliers = multsWithoutVuln;
                continue; 
            }

            // STANDARD PEEL
            decimal multsWithout = activeMultipliers / multMod.Amount;
            decimal damageWithout = Math.Max(0, Math.Floor(basePlusAdditives * multsWithout));
            
            decimal theoreticalDiff = currentCalculatedDamage - damageWithout;

            if (theoreticalDiff > 0)
            {
                decimal penalty = Math.Min(theoreticalDiff, overkill);
                decimal awardedDamage = theoreticalDiff - penalty;
                overkill -= penalty;

                if (awardedDamage > 0)
                {
                    var paid = PayoutMultiplierDamage(multMod.PowerId, awardedDamage, snapshot.Target, snapshot.Dealer, multMod.PowerInstance);
                    if (!paid)
                    {
                        extraDamage += awardedDamage;
                        GD.Print($"[DeckTracker] ProcessDamageSnapshot. Unattributed {awardedDamage} damage from {multMod.PowerId} routed to Base Card.");
                    }
                }
                
                currentCalculatedDamage = damageWithout;
                activeMultipliers = multsWithout; 
            }
        }
        
        // --- ADDITIVE PEEL (Second) ---
        for (int i = snapshot.AdditiveModifiers.Count - 1; i >= 0; i--)
        {
            var addMod = snapshot.AdditiveModifiers[i];

            decimal damageWithout = snapshot.BaseDamage;
            for (int j = 0; j < i; j++)
            {
                damageWithout += snapshot.AdditiveModifiers[j].Amount;
            }
            
            // CRITICAL FIX: Multiply by the activeMultipliers (which contains the Debuffs!)
            damageWithout = Math.Max(0, Math.Floor(damageWithout * activeMultipliers));

            decimal theoreticalDiff = currentCalculatedDamage - damageWithout;

            decimal penalty = Math.Min(theoreticalDiff, overkill);
            decimal awardedDamage = theoreticalDiff - penalty;
            overkill -= penalty;

            if (awardedDamage > 0)
            {
                var paid = PayoutAdditiveDamage(addMod.PowerId, awardedDamage);
                if (!paid)
                {
                    // UNTRACKED POWER DETECTED! Route it back to the Base Card!
                    extraDamage += awardedDamage;
                    GD.Print($"[DeckTracker] ProcessDamageSnapshot. Unattributed {awardedDamage} damage from {addMod.PowerId} routed to Base Card.");
                }
            }

            currentCalculatedDamage = damageWithout;
        }

        return Math.Max(0, currentCalculatedDamage - overkill + extraDamage);
    }
    
    // Returns true if it found a card to payout to, false if it did not (un-attributed environmental damage -- e.g. slow)
    private static bool PayoutMultiplierDamage(string powerId, decimal amount, Creature? target, Creature? dealer, PowerModel? powerInstance = null)
    {
        GD.Print($"[DeckTracker] PayoutMultiplierDamage. Power: {powerId}, Amount: {amount}");
        if (RelicLedger.ContainsKey(powerId) || powerId == "PEN_NIB" || powerId == "PAPER_PHROG")
        {
            AddRelicDamage(powerId, amount);
            return true;
        }
        
        // 0. Instanced Power Precision Routing (Flanking & Knockdown)
        if (powerInstance != null)
        {
            var instId = InstancedTracker.GetIdForInstance(powerInstance);
            if (instId != null)
            {
                AddDamageById(instId, amount);
                GD.Print($"[DeckTracker] PayoutMultiplierDamage (Instanced). Paid {amount} to {instId}");
                return true;
            }
        }
        
        // 1. Is it a Target Debuff? (Vulnerable)
        if (target != null && DurationLedgers.TryGetValue(target, out var targetLedger) 
                           && targetLedger.TryGetValue(powerId, out var enemyLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in enemyLedger)
            {
                if (remainingToPay <= 0)
                {
                    break;
                }
                decimal payout = remainingToPay; // 100% to the active turn!
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                GD.Print($"[DeckTracker] PayoutMultiplierDamage (Target Duration). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 2. Is it a Dealer Duration Buff? (Double Damage)
        if (dealer != null && DurationLedgers.TryGetValue(dealer, out var dealerLedger) 
                           && dealerLedger.TryGetValue(powerId, out var playerDurationLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in playerDurationLedger)
            {
                if (remainingToPay <= 0)
                {
                    break;
                }
                decimal payout = remainingToPay; // 100% to the active turn!
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                GD.Print($"[DeckTracker] PayoutMultiplierDamage (Dealer Duration). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 3. Is it a Consumable Player Buff? (e.g., Pen Nib)
        if (ConsumableLedgers.TryGetValue(powerId, out var consumableLedger))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in consumableLedger)
            {
                if (remainingToPay <= 0)
                {
                    break;
                }
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                GD.Print($"[DeckTracker] PayoutMultiplierDamage (Consumable). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }
        
        // 4. Is it a Persistent Player Buff? (e.g., A passive Stance or Relic modifier)
        if (PersistentLedgers.TryGetValue(powerId, out var persistentLedger))
        {
            decimal totalPool = persistentLedger.Sum(c => c.Amount);
        
            if (totalPool > 0)
            {
                decimal remainingToPay = amount;
            
                for (int i = 0; i < persistentLedger.Count; i++)
                {
                    var contribution = persistentLedger[i];
                    if (remainingToPay <= 0)
                    {
                        break;
                    }

                    if (i == persistentLedger.Count - 1)
                    {
                        AddDamageById(contribution.TrackingId, remainingToPay);
                        GD.Print($"[DeckTracker] PayoutMultiplierDamage (Persistent Remainder). Paid {remainingToPay} to {contribution.TrackingId}");
                        break;
                    }

                    decimal share = Math.Min(remainingToPay, Math.Ceiling(amount * (contribution.Amount / totalPool)));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        remainingToPay -= share;
                        GD.Print($"[DeckTracker] PayoutMultiplierDamage (Persistent Share). Paid {share} to {contribution.TrackingId}");
                    }
                }
            }

            return true;
        }

        return false;
    }
    
    private static bool PayoutAdditiveDamage(string powerId, decimal amount)
    {
        GD.Print($"[DeckTracker] PayoutAdditiveDamage. Power: {powerId}, Amount: {amount}");
        if (RelicLedger.ContainsKey(powerId) || powerId == "STRIKE_DUMMY" || powerId == "FAKE_STRIKE_DUMMY" 
            || powerId == "MYSTIC_LIGHTER" || powerId == "MINIATURE_CANNON")
        {
            AddRelicDamage(powerId, amount);
            return true;
        }
        
        // 1. Is it a Consumable? (Vigor)
        if (ConsumableLedgers.ContainsKey(powerId))
        {
            decimal remainingToPay = amount;
            foreach (var contribution in ConsumableLedgers[powerId])
            {
                if (remainingToPay <= 0)
                {
                    break;
                }
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                
                AddDamageById(contribution.TrackingId, payout);
                GD.Print($"[DeckTracker] PayoutAdditiveDamage (Consumable). Paid {payout} to {contribution.TrackingId}");
                
                remainingToPay -= payout;
            }

            return true;
        }
        // 2. Is it a Persistent Buff? (Strength, Accuracy, PhantomBlades)
        if (PersistentLedgers.TryGetValue(powerId, out var persistentLedger))
        {
            decimal totalPool = persistentLedger.Sum(c => c.Amount);
        
            if (totalPool > 0)
            {
                decimal remainingToPay = amount;
            
                for (int i = 0; i < persistentLedger.Count; i++)
                {
                    var contribution = persistentLedger[i];
                    if (remainingToPay <= 0)
                    {
                        break;
                    }

                    if (i == persistentLedger.Count - 1)
                    {
                        AddDamageById(contribution.TrackingId, remainingToPay);
                        GD.Print($"[DeckTracker] PayoutAdditiveDamage (Persistent Remainder). Paid {remainingToPay} to {contribution.TrackingId}");
                        break;
                    }

                    decimal share = Math.Min(remainingToPay, Math.Ceiling(amount * (contribution.Amount / totalPool)));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        remainingToPay -= share;
                        GD.Print($"[DeckTracker] PayoutAdditiveDamage (Persistent Share). Paid {share} to {contribution.TrackingId}");
                    }
                }
            }
            return true;
        }

        return false;
    }
}