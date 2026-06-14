using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Models.Relics;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static decimal ProcessDamageSnapshot(DamageSnapshot snapshot, decimal actualDealtDamage)
    {
        var basePlusAdditives = snapshot.BaseDamage;
        foreach (var a in snapshot.AdditiveModifiers)
        {
            basePlusAdditives += a.Amount;
        }

        var totalMultipliers = 1m;
        foreach (var m in snapshot.MultiplicativeModifiers)
        {
            totalMultipliers *= m.Amount;
        }

        var currentCalculatedDamage = Math.Max(0, Math.Floor(basePlusAdditives * totalMultipliers));
        var overkill = Math.Max(0, currentCalculatedDamage - actualDealtDamage);
        var extraDamage = Math.Max(0, actualDealtDamage - currentCalculatedDamage);

        // A rolling multiplier pool. Debuffs will permanently stay in this pool.
        var activeMultipliers = totalMultipliers;

        Log.Debug($"ProcessDamageSnapshot. Total Calculated: {currentCalculatedDamage}, Actual: {actualDealtDamage}, Overkill: {overkill}, Extra: {extraDamage}");

        // --- MULTIPLIER PEEL (First) ---
        for (int i = snapshot.MultiplicativeModifiers.Count - 1; i >= 0; i--)
        {
            var multMod = snapshot.MultiplicativeModifiers[i];

            // Vulnerable is a composite multiplier: base VulnPower → PaperPhrog → CrueltyPower → DebilitatePower.
            // We reconstruct each sub-layer by calling the game's own modifier chain in forward order,
            // then peel them in reverse so each contributor gets credit for exactly its marginal damage.
            if (multMod.PowerId == "VULNERABLE_POWER")
            {
                var vulnPower = snapshot.Target?.GetPower<VulnerablePower>();
                var phrog = snapshot.Dealer?.Player?.GetRelic<PaperPhrog>();
                var cruelty = snapshot.Dealer?.GetPower<CrueltyPower>();
                var debilitate = snapshot.Target?.GetPower<DebilitatePower>();

                var m_base =vulnPower != null ? vulnPower.DynamicVars["DamageIncrease"].BaseValue : 1.5m;
                var m_phrog = phrog != null
                    ? phrog.ModifyVulnerableMultiplier(snapshot.Target!, m_base, snapshot.Props, snapshot.Dealer, snapshot.CardSource)
                    : m_base;
                var m_cruel = cruelty != null
                    ? cruelty.ModifyVulnerableMultiplier(snapshot.Target!, m_phrog, snapshot.Props, snapshot.Dealer, snapshot.CardSource)
                    : m_phrog;

                var multsWithoutVuln = activeMultipliers / multMod.Amount;

                void PeelSubMultiplier(string id, decimal multWithout)
                {
                    var dmgWithout = Math.Max(0, Math.Floor(basePlusAdditives * (multsWithoutVuln * multWithout)));
                    var diff = currentCalculatedDamage - dmgWithout;
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
                                Log.Warn($"ProcessDamageSnapshot. Unattributed {awarded} damage from {id} routed to Base Card.");
                            }
                        }
                    }
                    currentCalculatedDamage = dmgWithout;
                }

                // Reverse execution order: Debilitate -> Cruelty -> Phrog -> Base
                if (debilitate != null) PeelSubMultiplier("DEBILITATE_POWER", m_cruel);
                if (cruelty != null) PeelSubMultiplier("CRUELTY_POWER", m_phrog);
                if (phrog != null) PeelSubMultiplier("PAPER_PHROG", m_base);
                PeelSubMultiplier("VULNERABLE_POWER", 1m);

                activeMultipliers = multsWithoutVuln;
                continue;
            }

            // STANDARD PEEL
            var multsWithout = activeMultipliers / multMod.Amount;
            var damageWithout = Math.Max(0, Math.Floor(basePlusAdditives * multsWithout));
            var theoreticalDiff = currentCalculatedDamage - damageWithout;

            if (theoreticalDiff > 0)
            {
                decimal penalty = Math.Min(theoreticalDiff, overkill);
                var awardedDamage = theoreticalDiff - penalty;
                overkill -= penalty;

                if (awardedDamage > 0)
                {
                    var paid = PayoutMultiplierDamage(multMod.PowerId, awardedDamage, snapshot.Target, snapshot.Dealer, multMod.PowerInstance);
                    if (!paid)
                    {
                        extraDamage += awardedDamage;
                        Log.Warn($"ProcessDamageSnapshot. Unattributed {awardedDamage} damage from {multMod.PowerId} routed to Base Card.");
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

            // Multiply by activeMultipliers (which contains the Debuffs!)
            damageWithout = Math.Max(0, Math.Floor(damageWithout * activeMultipliers));

            var theoreticalDiff = currentCalculatedDamage - damageWithout;
            decimal penalty = Math.Min(theoreticalDiff, overkill);
            var awardedDamage = theoreticalDiff - penalty;
            overkill -= penalty;

            if (awardedDamage > 0)
            {
                var paid = PayoutAdditiveDamage(addMod.PowerId, awardedDamage);
                if (!paid)
                {
                    extraDamage += awardedDamage;
                    Log.Warn($"ProcessDamageSnapshot. Unattributed {awardedDamage} damage from {addMod.PowerId} routed to Base Card.");
                }
            }

            currentCalculatedDamage = damageWithout;
        }

        return Math.Max(0, currentCalculatedDamage - overkill + extraDamage);
    }

    // Returns true if it found a card to payout to, false if not (un-attributed environmental damage — e.g. Slow).
    private static bool PayoutMultiplierDamage(string powerId, decimal amount, Creature? target, Creature? dealer, PowerModel? powerInstance = null)
    {
        Log.VeryDebug($"PayoutMultiplierDamage. Power: {powerId}, Amount: {amount}");

        if (EntityLedger.ContainsKey("RELIC_" + powerId) || powerId == "PEN_NIB" || powerId == "PAPER_PHROG")
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
                Log.VeryDebug($"PayoutMultiplierDamage (Instanced). Paid {amount} to {instId}");
                return true;
            }
        }

        // 1. Target Debuff (Vulnerable)
        if (target != null && DurationLedgers.TryGetValue(target, out var targetLedger)
                           && targetLedger.TryGetValue(powerId, out var enemyLedger))
        {
            var remainingToPay = amount;
            foreach (var contribution in enemyLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = remainingToPay;
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                Log.VeryDebug($"PayoutMultiplierDamage (Target Duration). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 2. Dealer Duration Buff (Double Damage)
        if (dealer != null && DurationLedgers.TryGetValue(dealer, out var dealerLedger)
                           && dealerLedger.TryGetValue(powerId, out var playerDurationLedger))
        {
            var remainingToPay = amount;
            foreach (var contribution in playerDurationLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = remainingToPay;
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                Log.VeryDebug($"PayoutMultiplierDamage (Dealer Duration). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 3. Consumable Player Buff (Pen Nib)
        if (ConsumableLedgers.TryGetValue(powerId, out var consumableLedger))
        {
            var remainingToPay = amount;
            foreach (var contribution in consumableLedger)
            {
                if (remainingToPay <= 0) break;
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                Log.VeryDebug($"PayoutMultiplierDamage (Consumable). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 4. Persistent Player Buff (Strength, Accuracy, PhantomBlades)
        if (PersistentLedgers.TryGetValue(powerId, out var persistentLedger))
        {
            var totalPool = persistentLedger.Sum(c => c.Amount);
            if (totalPool > 0)
            {
                var remainingToPay = amount;
                for (int i = 0; i < persistentLedger.Count; i++)
                {
                    var contribution = persistentLedger[i];
                    if (remainingToPay <= 0) break;

                    if (i == persistentLedger.Count - 1)
                    {
                        AddDamageById(contribution.TrackingId, remainingToPay);
                        Log.VeryDebug($"PayoutMultiplierDamage (Persistent Remainder). Paid {remainingToPay} to {contribution.TrackingId}");
                        break;
                    }

                    decimal share = Math.Min(remainingToPay, Math.Ceiling(amount * (contribution.Amount / totalPool)));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        remainingToPay -= share;
                        Log.VeryDebug($"PayoutMultiplierDamage (Persistent Share). Paid {share} to {contribution.TrackingId}");
                    }
                }
            }
            return true;
        }

        return false;
    }

    private static bool PayoutAdditiveDamage(string powerId, decimal amount)
    {
        Log.VeryDebug($"PayoutAdditiveDamage. Power: {powerId}, Amount: {amount}");

        if (EntityLedger.ContainsKey("RELIC_" + powerId) || powerId == "STRIKE_DUMMY" || powerId == "FAKE_STRIKE_DUMMY"
            || powerId == "MYSTIC_LIGHTER" || powerId == "MINIATURE_CANNON")
        {
            AddRelicDamage(powerId, amount);
            return true;
        }

        // 1. Consumable (Vigor)
        if (ConsumableLedgers.ContainsKey(powerId))
        {
            var remainingToPay = amount;
            foreach (var contribution in ConsumableLedgers[powerId])
            {
                if (remainingToPay <= 0) break;
                decimal payout = Math.Min(remainingToPay, contribution.Amount);
                AddDamageById(contribution.TrackingId, payout);
                remainingToPay -= payout;
                Log.VeryDebug($"PayoutAdditiveDamage (Consumable). Paid {payout} to {contribution.TrackingId}");
            }
            return true;
        }

        // 2. Persistent Buff (Strength, Accuracy, PhantomBlades)
        if (PersistentLedgers.TryGetValue(powerId, out var persistentLedger))
        {
            var totalPool = persistentLedger.Sum(c => c.Amount);
            if (totalPool > 0)
            {
                var remainingToPay = amount;
                for (int i = 0; i < persistentLedger.Count; i++)
                {
                    var contribution = persistentLedger[i];
                    if (remainingToPay <= 0) break;

                    if (i == persistentLedger.Count - 1)
                    {
                        AddDamageById(contribution.TrackingId, remainingToPay);
                        Log.VeryDebug($"PayoutAdditiveDamage (Persistent Remainder). Paid {remainingToPay} to {contribution.TrackingId}");
                        break;
                    }

                    decimal share = Math.Min(remainingToPay, Math.Ceiling(amount * (contribution.Amount / totalPool)));
                    if (share > 0)
                    {
                        AddDamageById(contribution.TrackingId, share);
                        remainingToPay -= share;
                        Log.VeryDebug($"PayoutAdditiveDamage (Persistent Share). Paid {share} to {contribution.TrackingId}");
                    }
                }
            }
            return true;
        }

        return false;
    }
}
