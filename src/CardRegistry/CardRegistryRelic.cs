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

            var actData = _currentAct switch
            {
                1 => stats.Act1,
                2 => stats.Act2,
                3 => stats.Act3,
                4 => stats.Act4,
                _ => null
            };

            if (actData != null)
            {
                switch (_currentCombatType)
                {
                    case "Elite":
                        actData.DamageElite += amount;
                        break;
                    case "Boss":
                        actData.DamageBoss += amount;
                        break;
                    case "Hallway":
                        actData.DamageHallway += amount;
                        break;
                }
            }

            GD.Print($"[DeckTracker] Added {amount} damage to Relic: {relicId}");
        }

        Publish();
    }

    private static RelicStats GetOrCreateRelicStats(string relicId)
    {
        if (!RelicLedger.TryGetValue(relicId, out var stats))
        {
            string displayName;
            
            if (RelicNameCache.TryGetValue(relicId, out var cachedName))
            {
                displayName = cachedName;
            }
            else
            {
                // Fallback 1: SCREAMING_SNAKE_CASE to Title Case (e.g., "STRIKE_DUMMY" -> "Strike Dummy")
                displayName = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(relicId.ToLower().Replace('_', ' '));
                
                // Fallback 2: PascalCase to Title Case (Kept for legacy tracking variables)
                displayName = System.Text.RegularExpressions.Regex.Replace(displayName, "([a-z])([A-Z])", "$1 $2");
            }

            stats = new RelicStats { Id = relicId, DisplayName = displayName };
            RelicLedger[relicId] = stats;
        }
        return stats;
    }
}