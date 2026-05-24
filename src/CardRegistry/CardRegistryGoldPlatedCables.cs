using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public partial class CardRegistry
{
    // Tracks how many times an orb has naturally fired its passive this turn
    public static readonly Dictionary<OrbModel, int> EotPassiveCounts = new();

    // Call this at the start of the player's turn to reset the counters!
    public static void ResetOrbTurnState()
    {
        lock (SyncRoot)
        {
            EotPassiveCounts.Clear();
            CurrentTurnLoopQueue.Clear(); // (If you still use this for Loop)
            GD.Print("[DeckTracker] ResetOrbTurnState");
        }
    }
}