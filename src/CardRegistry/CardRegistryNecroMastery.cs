using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    private static readonly List<string> _necroMasteryLedger = new();
    // Unlike the other execution flags, these stay AsyncLocal on purpose. Necro Mastery deals its
    // damage in-flow (AfterCurrentHpChanged awaits CreatureCmd.Damage), and that enemy hit re-fires
    // AfterCurrentHpChanged, opening a second overlapping window. A shared field would let the inner
    // window's finally clear the flag before the real damage lands; AsyncLocal isolates each flow and
    // still flows into the awaited damage, so it is both correct and necessary here.
    private static readonly AsyncLocal<bool> _isNecroMasteryExecuting = new();
    private static AsyncLocal<decimal> _delta = new();

    public static bool IsNecroMasteryExecuting => _isNecroMasteryExecuting.Value;

    public static void StartNecroMasteryExecution(decimal delta)
    {
        _isNecroMasteryExecuting.Value = true;
        _delta.Value = delta;
    }

    public static void ResetNecroMasteryState()
    {
        lock (SyncRoot)
        {
            _necroMasteryLedger.Clear();
            _delta = new AsyncLocal<decimal>();
        }
    }

    public static void LogNecroMasteryApply(CardModel? cardSource, int amount)
    {
        if (amount <= 0) return;

        lock (SyncRoot)
        {
            var trackingId = cardSource != null ? GetTrackingId(cardSource) : "External_Source";
            // One entry per stack: DistributeNecroMasteryDamage caps each entry at the pet's HP
            // loss (-delta) while total damage dealt is -delta * Amount, so Amount stacks need
            // Amount contributions or the surplus damage would be left unattributed.
            for (var i = 0; i < amount; i++)
            {
                _necroMasteryLedger.Add(trackingId);
            }
            Log.Debug($"LogNecroMasteryApply. Card: {trackingId}, Amount: {amount}");
        }
    }

    public static void DistributeNecroMasteryDamage(decimal totalDamage)
    {
        if (totalDamage <= 0) return;

        lock (SyncRoot)
        {
            var remainingDamage = totalDamage;
            Log.Debug($"DistributeNecroMasteryDamage. Total Damage: {totalDamage} with delta: {_delta.Value}");

            foreach (var trackingId in _necroMasteryLedger)
            {
                if (remainingDamage <= 0) break;
                var share = Math.Min(remainingDamage, -_delta.Value);
                AddDamageById(trackingId, share);
                remainingDamage -= share;
                Log.Debug($"DistributeNecroMasteryDamage. Attributed {share} to {trackingId}. Remaining: {remainingDamage}");
            }

            if (remainingDamage > 0)
            {
                Log.Warn($"DistributeNecroMasteryDamage. {remainingDamage} damage unattributed (more stacks than contributions?)");
            }
        }
    }

    public static async Task AwaitNecroMasteryTaskAsync(Task originalTask, decimal delta)
    {
        try
        {
            StartNecroMasteryExecution(delta);
            await originalTask;
        }
        finally
        {
            _isNecroMasteryExecuting.Value = false;
            Log.VeryDebug("AwaitNecroMasteryTaskAsync finished.");
        }
    }
}
