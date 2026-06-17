using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // --- CONTEXT CLASSES ---
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

    // --- BUFF ROUTING REGISTRIES ---
    // Powers whose only behaviour is add/remove a persistent player buff.
    public static readonly HashSet<string> PersistentBuffPowerIds = new()
    {
        "DEMON_FORM_POWER", "ARSENAL_POWER", "SHADOW_STEP_POWER", "ACCURACY_POWER",
        "PHANTOM_BLADES_POWER", "PREP_TIME_POWER", "CRUELTY_POWER", "LETHALITY_POWER", "CALCIFY_POWER",
    };

    // Powers whose only behaviour is add/remove a duration debuff on a target creature.
    public static readonly HashSet<string> DurationDebuffPowerIds = new()
    {
        "VULNERABLE_POWER", "DEBILITATE_POWER", "GIGANTIFICATION_POWER", "FLANKING_POWER", "KNOCKDOWN_POWER",
    };

    // --- STATE VARIABLES ---
    // Maps a specific Creature to their active debuffs (like Vulnerable)
    public static readonly Dictionary<Creature, Dictionary<string, List<Contribution>>> EnemyDebuffLedgers = new();
    public static readonly Dictionary<string, List<Contribution>> PersistentLedgers = new();
    public static readonly Dictionary<string, List<Contribution>> ConsumableLedgers = new();
    
    // The Snapshot trap we will use in Phase 3
    public static readonly AsyncLocal<DamageSnapshot?> CurrentAttackSnapshot = new();

    public static void RouteStrengthApplication(Creature target, string powerId, decimal amount, CardModel? cardSource)
    {
        if (!target.IsPlayer) return;

        if (amount > 0)
        {
            var handoff = HandoffTrackers.Values.FirstOrDefault(t => t.IsExecuting);
            if (handoff != null)
            {
                handoff.ProcessHandoff(powerId, amount);
                return;
            }

            if (InstancedTracker.ExecutingSourceId != null)
            {
                AddPersistentBuffById(powerId, amount, InstancedTracker.ExecutingSourceId);
                return;
            }

            if (IsRitualTriggering.Value)
            {
                foreach (var s in RitualSources)
                {
                    if (s.Value > 0)
                    {
                        AddPersistentBuffById(powerId, s.Value, s.Key);
                    }
                }
                return;
            }

            var executingProp = ProportionalTrackers.Values.FirstOrDefault(t => t.IsExecuting);
            if (executingProp != null)
            {
                executingProp.DistributeProportional(amount, (id, amt) => AddPersistentBuffById(powerId, amt, id), "Persistent Handoff");
            }
            else
            {
                AddPersistentBuffById(powerId, amount, GetCurrentSourceId(cardSource));
            }
        }
        else if (amount < 0)
        {
            RemovePersistentBuff(powerId, Math.Abs(amount));
        }
    }

    public static void ResetBuffState()
    {
        lock (SyncRoot)
        {
            PersistentLedgers.Clear();
            ConsumableLedgers.Clear();
            EnemyDebuffLedgers.Clear();
            Log.Debug("ResetBuffState. All buff ledgers cleared.");
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
                ConsumableLedgers[buffType] = new List<Contribution>();
            }
            ConsumableLedgers[buffType].Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"AddConsumableBuffById. Added {amount} {buffType} to Consumable FIFO ledger for {trackingId}");
        }
    }

    // --- ENEMY DEBUFF LEDGERS ---

    // --- DURATION LEDGERS (For Vulnerable, Double Damage, Weak, Intangible) ---
    // Maps a Creature (Player or Enemy) to their active turn-based buffs!
    public static readonly Dictionary<Creature, Dictionary<string, List<Contribution>>> DurationLedgers = new();

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
                DurationLedgers[target][buffType] = new List<Contribution>();
            }
            
            DurationLedgers[target][buffType].Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"AddDurationBuff. Added {amount} {buffType} to Duration FIFO ledger for {trackingId} on {target.Name}");
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
            Log.Debug($"RemoveDurationBuff. Removing {amount} {buffType} from {target.Name}");
            DrainLedger(ledger, amount, lifo: false);
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
                PersistentLedgers[buffType] = new List<Contribution>();
            }
            
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            PersistentLedgers[buffType].Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"AddPersistentBuff. Added {amount} {buffType} to persistent ledger for {trackingId}");
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
                PersistentLedgers[buffType] = new List<Contribution>();
            }
            PersistentLedgers[buffType].Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"AddPersistentBuffById. Added {amount} {buffType} to Persistent ledger for {trackingId}");
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
            Log.Debug($"RemovePersistentBuff. Removing {amount} {buffType}");
            // LIFO because temporary strength and debuffs expire in reverse-add order
            DrainLedger(ledger, amount, lifo: true);
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
                ConsumableLedgers[buffType] = new List<Contribution>();
            }
            
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Buff";
            ConsumableLedgers[buffType].Add(new Contribution { TrackingId = trackingId, Amount = amount });
            Log.Debug($"AddConsumableBuff. Added {amount} {buffType} to consumable FIFO ledger for {trackingId}");
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
            Log.Debug($"RemoveConsumableBuff. Consuming {amount} {buffType}");
            DrainLedger(ledger, amount, lifo: false);
        }
    }

    // Drains `amount` budget from a contribution ledger, erasing oldest-first (FIFO) or newest-first (LIFO).
    // The remaining > 0 guard lives in the for predicate to keep nesting flat.
    private static void DrainLedger(List<Contribution> ledger, decimal amount, bool lifo)
    {
        var remaining = amount;
        if (lifo)
        {
            for (int i = ledger.Count - 1; i >= 0 && remaining > 0; i--)
            {
                decimal erased = Math.Min(remaining, ledger[i].Amount);
                ledger[i].Amount -= erased;
                remaining -= erased;
                Log.VeryDebug($"  -> Erased {erased} from {ledger[i].TrackingId}");
            }
        }
        else
        {
            for (int i = 0; i < ledger.Count && remaining > 0; i++)
            {
                decimal erased = Math.Min(remaining, ledger[i].Amount);
                ledger[i].Amount -= erased;
                remaining -= erased;
                Log.VeryDebug($"  -> Erased {erased} from {ledger[i].TrackingId}");
            }
        }
        ledger.RemoveAll(c => c.Amount <= 0);
    }

}