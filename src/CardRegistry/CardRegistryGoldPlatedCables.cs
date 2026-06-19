using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Tracks how many times an orb has naturally fired its passive this turn
    public static readonly Dictionary<OrbModel, int> EotPassiveCounts = new();

    // Set by GoldPlatedCablesModifyOrbPassivePostfix when GPC increases an orb's passive count.
    // Consumed by OrbPassivePrefix on the extra passive fire for multiplayer-correct attribution.
    public static RelicModel? PendingExtraPassiveSource { get; set; }

    // Call this at the start of the player's turn to reset the counters!
    public static void ResetOrbTurnState()
    {
        lock (SyncRoot)
        {
            EotPassiveCounts.Clear();
            CurrentTurnLoopQueue.Clear();
            PendingExtraPassiveSource = null;
            Log.Debug("ResetOrbTurnState. Orb turn state cleared.");
        }
    }
}