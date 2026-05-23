using Godot;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- RELIC TRACKING ---
    // Maps a Relic Class Name (e.g. "PenNib", "MercuryHourglass") to its stats
    public static readonly Dictionary<string, RelicStats> RelicLedger = new();

    public static void AddRelicDamage(string relicId, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            if (!RelicLedger.TryGetValue(relicId, out var stats))
            {
                stats = new RelicStats { Id = relicId, DisplayName = relicId };
                RelicLedger[relicId] = stats;
            }
            stats.CombatDamage += amount;
            stats.RunDamage += amount;
            GD.Print($"[DeckTracker] Added {amount} damage to Relic: {relicId}");
        }
    }
}