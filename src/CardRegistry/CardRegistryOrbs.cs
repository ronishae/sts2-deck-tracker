using Godot;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Models.Powers;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly AsyncLocal<bool> IsApplyingTemporaryFocus = new();
    public static readonly AsyncLocal<bool> IsExpiringTemporaryFocus = new();
    
    private static readonly Dictionary<OrbModel, string> OrbChannelers = new();
    private static readonly List<FocusContribution> FocusHistory = new();
    
    private static readonly Dictionary<OrbModel, List<Contribution>> DarkOrbLedgers = new();
    
    public static readonly List<Contribution> LoopHistory = new();
    public static readonly List<string> CurrentTurnLoopQueue = new();
    public static bool IsLoopExecuting;
    
    // Tracks if an Orb is currently dealing damage
    private static OrbExecutionContext? _executingOrb;
    public static OrbExecutionContext? ExecutingOrb
    {
        get
        {
            return _executingOrb;
        }
        set
        {
            _executingOrb = value;
        }
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
            Log.Debug("ResetOrbState. State cleared.");
        }
    }

    public static void RegisterChanneledOrb(OrbModel orb, CardModel? sourceCard)
    {
        lock (SyncRoot)
        {
            string trackingId = sourceCard != null ? GetTrackingId(sourceCard) :
                (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId) ?
                    "RELIC_" + RelicExecutionManager.ExecutingRelicId :
                    "External_Relic");
            
            foreach (var queueTracker in QueueTrackers.Values)
            {
                if (queueTracker.IsExecuting)
                {
                    var nextId = queueTracker.GetNextIdForOrb();
                    if (nextId != null)
                    {
                        trackingId = nextId;
                        Log.Debug($"RegisterChanneledOrb. Routed {orb.Id.Entry} to QueueTracker {queueTracker.PowerId}: {trackingId}");
                    }
                    break; 
                }
            }
            
            if (sourceCard == null && !string.IsNullOrEmpty(CurrentPlayingPotionId))
            {
                trackingId = CurrentPlayingPotionId;
                Log.Debug($"RegisterChanneledOrb. Tagging channeled Orb to Potion: {trackingId}");
            }
            
            OrbChannelers[orb] = trackingId;
            Log.Debug($"RegisterChanneledOrb. Orb: {orb.Id.Entry}, Attributed To: {trackingId}");
            
            if (orb is DarkOrb)
            {
                DarkOrbLedgers[orb] = new List<Contribution>();
                DarkOrbLedgers[orb].Add(new Contribution { TrackingId = trackingId, Amount = orb.EvokeVal });
                Log.Debug($"RegisterChanneledOrb (Dark Orb). Initial Base: {orb.EvokeVal:F2}, To: {trackingId}");
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
                if (DarkOrbLedgers.ContainsKey(orb))
                {
                    DarkOrbLedgers.Remove(orb);
                }
                Log.Debug($"DeregisterOrb. Removed {orb.Id.Entry}.");
            }
        }
    }
    
    public static void AddLoop(decimal amount, CardModel? cardSource)
    {
        if (cardSource == null || amount <= 0)
        {
            return;
        }
        lock (SyncRoot)
        {
            var id = GetTrackingId(cardSource);
            LoopHistory.Add(new Contribution { TrackingId = id, Amount = amount });
            Log.Debug($"AddLoop. Amount: {amount}, Source: {id}");
        }
    }
    
    public static void RecordDarkOrbWave(OrbModel orb, decimal waveAmount, string? forcedActorId = null)
    {
        lock (SyncRoot)
        {
            if (!DarkOrbLedgers.TryGetValue(orb, out var ledger))
            {
                return;
            }

            decimal totalFocus = 0;
            foreach (var focus in FocusHistory)
            {
                totalFocus += focus.Amount;
            }

            decimal pureBase = waveAmount - totalFocus;
            if (totalFocus < 0)
            {
                pureBase = waveAmount;
                totalFocus = 0;
            }

            if (pureBase > 0)
            {
                string? baseId = forcedActorId;
                if (baseId == null && OrbChannelers.TryGetValue(orb, out var channeler))
                {
                    baseId = channeler;
                }
                if (baseId == null)
                {
                    baseId = "External_Relic";
                }

                ledger.Add(new Contribution { TrackingId = baseId, Amount = pureBase });
                Log.Debug($"RecordDarkOrbWave. Base: {pureBase:F2}, To: {baseId}");
            }
            if (totalFocus > 0)
            {
                foreach (var focus in FocusHistory)
                {
                    if (focus.Amount > 0)
                    {
                        ledger.Add(new Contribution { TrackingId = focus.TrackingId, Amount = focus.Amount });
                        Log.Debug($"RecordDarkOrbWave. Focus: {focus.Amount:F2}, To: {focus.TrackingId}");
                    }
                }
            }
        }
    }
    
    public static void LogFocusChange(CardModel? cardSource, decimal amount)
    {
        string trackingId = cardSource != null ? GetTrackingId(cardSource) :
            (!string.IsNullOrEmpty(RelicExecutionManager.ExecutingRelicId) ?
                "RELIC_" + RelicExecutionManager.ExecutingRelicId :
                (amount > 0 ? "External_Buff" : "External_Debuff"));
        
        Log.Debug($"LogFocusChange (Forwarder). TrackingId: {trackingId}, Amount: {amount}");
        LogFocusChangeById(trackingId, amount);
    }

    public static void LogFocusChangeById(string trackingId, decimal amount)
    {
        if (amount == 0)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (IsExpiringTemporaryFocus.Value)
            {
                decimal rem = Math.Abs(amount);
                Log.Debug($"LogFocusChangeById (Erasing). Amount: {rem}");
                for (int i = 0; i < FocusHistory.Count; i++)
                {
                    if (rem <= 0)
                    {
                        break;
                    }
                    var c = FocusHistory[i];
                    if (c.Amount > 0 && c.IsTemporary)
                    {
                        decimal e = Math.Min(rem, c.Amount);
                        c.Amount -= e;
                        rem -= e;
                        Log.VeryDebug($"  -> Erased {e} from {c.TrackingId}");
                    }
                }
                FocusHistory.RemoveAll(c => c.Amount == 0);
                return;
            }
            
            FocusHistory.Add(new FocusContribution { 
                TrackingId = trackingId, 
                Amount = amount, 
                IsTemporary = IsApplyingTemporaryFocus.Value 
            });
            Log.Debug($"LogFocusChangeById. Amount: {amount}, Source: {trackingId}, Temp: {IsApplyingTemporaryFocus.Value}");
        }
    }
    
    public static void DistributeOrbDamage(OrbExecutionContext context, decimal totalDamage, Creature player)
    {
        if (totalDamage <= 0)
        {
            return;
        }

        // Generic check for SimpleDamageTrackers (handles Thunder)
        var executingSimple = SimpleDamageTrackers.Values.FirstOrDefault(t => t.IsExecuting);
        if (executingSimple != null)
        {
            Log.Debug($"DistributeOrbDamage. Redirecting {totalDamage} to SimpleTracker {executingSimple.PowerId}");
            executingSimple.DistributeDamage(totalDamage);
            return;
        }

        lock (SyncRoot)
        {
            var remaining = totalDamage;
            var totalRelicMods = 0m;
            Log.Debug($"DistributeOrbDamage (Waterfall). Total: {totalDamage}, Orb: {context.Orb.Id.Entry}");

            if (RelicExecutionManager.PendingOrbModifiers.Value != null)
            {
                foreach (var mod in RelicExecutionManager.PendingOrbModifiers.Value)
                {
                    decimal payout = Math.Min(remaining, mod.delta);
                    AddRelicDamage(mod.relicId, payout);
                    remaining -= payout;
                    totalRelicMods += mod.delta;
                    Log.VeryDebug($"  -> Peeled {payout} to Relic {mod.relicId}");
                }
                RelicExecutionManager.PendingOrbModifiers.Value.Clear();
            }

            OrbModel orb = context.Orb;
            if (!OrbChannelers.TryGetValue(orb, out string? channelerId))
            {
                if (context.IsEvoke && CurrentPlayingCard != null)
                {
                    var multiId = GetTrackingId(CurrentPlayingCard);
                    AddDamageById(multiId, remaining);
                    Log.Debug($"DistributeOrbDamage (Phantom Evoke). Attributed {remaining} to {multiId}");
                    return;
                }
                UnattributedOrbLogs.Add($"Unattributed {orb.Id.Entry} Orb Damage ({remaining}). Cause: No channeling card found.");
                Log.Warn($"DistributeOrbDamage (UNATTRIBUTED). Amount: {remaining}");
                return;
            }
            
            if (orb is DarkOrb)
            {
                if (DarkOrbLedgers.TryGetValue(orb, out var ledger))
                {
                    Log.Debug($"DistributeOrbDamage (Dark FIFO). Remaining: {remaining}");
                    foreach (var c in ledger)
                    {
                        if (remaining <= 0)
                        {
                            break;
                        }
                        decimal p = Math.Min(remaining, c.Amount);
                        AddDamageById(c.TrackingId, p);
                        remaining -= p;
                        Log.VeryDebug($"  -> Attributed {p} to {c.TrackingId}");
                    }
                }
                return;
            }

            decimal totalFocus = player.GetPower<FocusPower>()?.Amount ?? 0m;
            decimal pureBase = context.ExpectedValue - totalFocus - totalRelicMods;
            decimal cPayout;
            decimal fPayoutTotal;

            if (totalFocus >= 0)
            {
                cPayout = Math.Min(remaining, pureBase);
                fPayoutTotal = remaining - cPayout;
            }
            else
            {
                cPayout = Math.Min(remaining - totalFocus, pureBase);
                fPayoutTotal = remaining - cPayout;
            }

            if (cPayout != 0)
            {
                var baseTarget =(!context.IsEvoke && context.ForcedActorId != null) ? context.ForcedActorId : channelerId;
                AddDamageById(baseTarget, cPayout);
                Log.VeryDebug($"  -> Base Payout: {cPayout}, To: {baseTarget}");
            }

            if (fPayoutTotal != 0)
            {
                foreach (var c in FocusHistory)
                {
                    if (fPayoutTotal == 0)
                    {
                        break;
                    }
                    if (fPayoutTotal > 0 && c.Amount > 0)
                    {
                        decimal p = Math.Min(fPayoutTotal, c.Amount);
                        AddDamageById(c.TrackingId, p);
                        fPayoutTotal -= p;
                        Log.VeryDebug($"  -> Focus Payout: {p}, To: {c.TrackingId}");
                    }
                    else if (fPayoutTotal < 0 && c.Amount < 0)
                    {
                        decimal p = Math.Max(fPayoutTotal, c.Amount);
                        AddDamageById(c.TrackingId, p);
                        fPayoutTotal -= p;
                        Log.VeryDebug($"  -> Focus Debt: {p}, To: {c.TrackingId}");
                    }
                }
            }
            
            if (fPayoutTotal > 0)
            {
                Log.Warn($"DistributeOrbDamage (Leak). {fPayoutTotal} damage unattributed.");
            }
        }
        Publish();
    }
    
    public static async Task AwaitOrbExecutionTaskAsync(Task originalTask, OrbModel orb, bool isEvoke)
    {
        var context = ExecutingOrb;
        try
        {
            await originalTask;
            if (!isEvoke && orb is DarkOrb && context != null)
            {
                Log.Debug($"AwaitOrbExecutionTaskAsync. Recording Dark Orb Wave for {orb.Id.Entry}");
                RecordDarkOrbWave(orb, context.ExpectedValue, context.ForcedActorId);
            }
        }
        finally
        {
            ExecutingOrb = null;
            if (isEvoke)
            {
                DeregisterOrb(orb);
            }
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
        try
        {
            await originalTask;
        }
        finally
        {
            IsApplyingTemporaryFocus.Value = false;
            Log.VeryDebug("AwaitTempFocusApplyAsync finished.");
        }
    }

    public static async Task AwaitTempFocusExpireAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsExpiringTemporaryFocus.Value = false;
            Log.VeryDebug("AwaitTempFocusExpireAsync finished.");
        }
    }
    
    public static async Task AwaitLoopTaskAsync(Task originalTask)
    {
        try
        {
            await originalTask;
        }
        finally
        {
            IsLoopExecuting = false;
            CurrentTurnLoopQueue.Clear();
            Log.VeryDebug("AwaitLoopTaskAsync finished.");
        }
    }
}