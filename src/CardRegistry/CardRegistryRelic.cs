using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public partial class CardRegistry
{
    // --- RELIC TRACKING ---
    // Maps a Relic Class Name (e.g. "PenNib", "MercuryHourglass") to its stats
    public static readonly Dictionary<string, RelicStats> RelicLedger = new();
    public static readonly Dictionary<string, string> RelicNameCache = new();

    public static void AddRelicDamage(string relicId, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            var stats = GetOrCreateRelicStats(relicId);
            stats.CombatDamage += amount;
            stats.RunDamage += amount;
            GD.Print($"[DeckTracker] Added {amount} damage to Relic: {relicId}");
        }

        Publish();
    }

    private static RelicStats GetOrCreateRelicStats(string relicId)
    {
        if (!RelicLedger.TryGetValue(relicId, out var stats))
        {
            // Check the cache first, otherwise use Regex fallback
            string displayName = RelicNameCache.TryGetValue(relicId, out var cachedName)
                ? cachedName
                : System.Text.RegularExpressions.Regex.Replace(relicId, "([a-z])([A-Z])", "$1 $2");

            stats = new RelicStats { Id = relicId, DisplayName = displayName };
            RelicLedger[relicId] = stats;
        }
        return stats;
    }
}