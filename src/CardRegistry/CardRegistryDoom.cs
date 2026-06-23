using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    

    // Tracks exactly who applied doom, in exact order
    private static readonly Dictionary<Creature, List<Contribution>> DoomHistory = new();
    
    // Captures HP right before CreatureCmd.Kill happens
    private static readonly Dictionary<Creature, decimal> PendingDoomHp = new();

    public static void RouteDoomApplication(Creature target, decimal amount, CardModel? cardSource)
    {
        if (amount <= 0) return;

        if (IsCountdownExecuting)
        {
            lock (SyncRoot)
            {
                var rem = amount;
                foreach (var c in CountdownHistory)
                {
                    if (rem <= 0) break;
                    var a = Math.Min(rem,c.Amount);
                    AddDoomHistoryById(target, a, c.TrackingId);
                    rem -= a;
                }
            }
            return;
        }

        if (IsReaperFormExecuting)
        {
            lock (SyncRoot)
            {
                var rem = amount;
                var dmg = GetReaperDamage();
                foreach (var s in ReaperFormShares)
                {
                    if (rem <= 0) break;
                    var a = Math.Min(rem,s.Amount * dmg);
                    AddDoomHistoryById(target, a, s.TrackingId);
                    rem -= a;
                }
            }
            return;
        }

        var oblivionContributions = CurrentOblivionContributions.Value;
        if (oblivionContributions != null && oblivionContributions.Count > 0)
        {
            DistributeDoomToHistory(target, amount, oblivionContributions);
        }
        else
        {
            AddDoomHistoryById(target, amount, GetCurrentSourceId(cardSource));
        }
    }

    public static void ResetDoomState()
    {
        lock (SyncRoot)
        {
            DoomHistory.Clear();
            PendingDoomHp.Clear();
            OblivionSourceMap.Clear();
        }
    }

    // Proportionally splits doom stack attribution across multiple contributors into DoomHistory.
    // The last contributor absorbs any rounding remainder to ensure the full amount is always recorded.
    private static void DistributeDoomToHistory(Creature target, decimal amount, List<Contribution> contributions)
    {
        var totalContributions = contributions.Sum(c => c.Amount);
        var remaining = amount;
        for (var i = 0; i < contributions.Count; i++)
        {
            if (remaining <= 0) break;
            var contribution = contributions[i];
            var share = i == contributions.Count - 1
                ? remaining
                : Math.Floor(amount * (contribution.Amount / totalContributions));
            var toAdd = Math.Min(remaining, share);
            if (toAdd > 0)
            {
                AddDoomHistoryById(target, toAdd, contribution.TrackingId);
                remaining -= toAdd;
            }
        }
        Log.Debug($"DistributeDoomToHistory. Target: {target.Name}, Amount: {amount}, Contributors: {contributions.Count}");
    }

    public static async Task AwaitOblivionTaskAsync(Task task)
    {
        try { await task; }
        finally { CurrentOblivionContributions.Value = null; }
    }

    public static void AddDoomHistory(Creature target, decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0) return;

        lock (SyncRoot)
        {
            if (!DoomHistory.ContainsKey(target)) DoomHistory[target] = new List<Contribution>();

            var uniqueId = GetTrackingId(cardSource);
            DoomHistory[target].Add(new Contribution { TrackingId = uniqueId, Amount = amount });
            Log.Debug($"Added {amount} Doom to FIFO queue for {uniqueId}.");
        }
        Publish();
    }
    
    public static void AddDoomHistoryById(Creature target, decimal amount, string uniqueId)
    {
        if (amount <= 0 || string.IsNullOrEmpty(uniqueId)) return;

        lock (SyncRoot)
        {
            if (!DoomHistory.ContainsKey(target)) DoomHistory[target] = new List<Contribution>();

            DoomHistory[target].Add(new Contribution { TrackingId = uniqueId, Amount = amount });
            Log.Debug($"Chained {amount} Doom to FIFO queue for {uniqueId}.");
        }
        Publish();
    }

    // Runs in the Prefix to grab the HP before it becomes 0
    public static void CapturePendingDoomHp(IReadOnlyList<Creature> creatures)
    {
        lock (SyncRoot)
        {
            foreach (var creature in creatures)
            {
                if (creature.CurrentHp > 0)
                {
                    PendingDoomHp[creature] = creature.CurrentHp;
                    Log.Debug($"Captured {creature.CurrentHp} HP for {creature.Name} before Doom execution.");
                }
            }
        }
    }

    // Runs in the AfterDiedToDoom hook to pay out the damage
    public static void DistributeDoomDamage(IReadOnlyList<Creature> creatures)
    {
        lock (SyncRoot)
        {
            foreach (var creature in creatures)
            {
                if (!PendingDoomHp.TryGetValue(creature, out var hpToDistribute) || hpToDistribute <= 0)
                {
                    continue;
                }
                PendingDoomHp.Remove(creature);
                
                if (!DoomHistory.TryGetValue(creature, out var history) || history.Count == 0)
                {
                    continue;
                }

                var remainingHp = hpToDistribute;
                
                foreach (var contribution in history)
                {
                    if (remainingHp <= 0) break;

                    var amountToAttribute = Math.Min(remainingHp, contribution.Amount);
                    
                    Log.Debug($"Attributing {amountToAttribute} Doom damage to {contribution.TrackingId}");
                    
                    AddDamageById(contribution.TrackingId, amountToAttribute);
                    
                    remainingHp -= amountToAttribute;
                }

                if (remainingHp > 0)
                {
                    Log.Warn($"DistributeDoomDamage. {remainingHp} Doom damage unaccounted for by card history.");
                }

                DoomHistory.Remove(creature);
            }
        }
        Publish();
    }
}