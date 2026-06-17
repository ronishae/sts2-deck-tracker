using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly Dictionary<string, string> RelicNameCache = new();

    public static void AddRelicDamage(string relicId, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            // GetOrCreateRelicStats ensures the entry exists in EntityLedger before AddCombatDamage runs.
            GetOrCreateRelicStats(relicId).AddCombatDamage(amount, _currentAct, _currentCombatType);
            Log.Debug($"Added {amount} damage to Relic: {relicId}");
        }
        Publish();
    }

    public static void HandleRelicRemove(RelicModel relic, int floorRemoved)
    {
        lock (SyncRoot)
        {
            var key = "RELIC_" + relic.Id.Entry;
            if (EntityLedger.TryGetValue(key, out var entity))
            {
                entity.FloorRemoved = floorRemoved;
                entity.IsActive = false;
                Log.Debug($"Handled removal for Relic: {relic.Id.Entry} on floor {floorRemoved}");
            }
        }
        Publish();
    }

    public static RelicStats GetOrCreateRelicStats(string relicId)
    {
        var key = "RELIC_" + relicId;
        if (!EntityLedger.TryGetValue(key, out var entity) || entity is not RelicStats stats)
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
            EntityLedger[key] = stats;
        }
        return stats;
    }
}