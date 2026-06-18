using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly Dictionary<string, string> RelicNameCache = new();

    // Maps each RelicModel instance to its owner's NetId.
    // Populated at relic-add and restore time; cleared in ResetRun.
    private static readonly Dictionary<RelicModel, string> _relicOwnerNetIdByModel = new();

    public static string? GetRelicOwnerNetId(RelicModel relic) =>
        _relicOwnerNetIdByModel.GetValueOrDefault(relic);

    public static void SetRelicOwnerNetId(RelicModel relic, string netId) =>
        _relicOwnerNetIdByModel[relic] = netId;

    // Returns "{id}_P{netId}" or "{id}" (no RELIC_ prefix) — matches ExecutingRelicId.Value format
    // so that "RELIC_" + ExecutingRelicId.Value always produces the correct ledger key.
    public static string GetRelicScopedId(RelicModel relic)
    {
        var netId = GetRelicOwnerNetId(relic);
        return netId != null ? $"{relic.Id.Entry}_P{netId}" : relic.Id.Entry;
    }

    public static string GetRelicLedgerKey(RelicModel relic) => "RELIC_" + GetRelicScopedId(relic);

    // Tries player-scoped key first (using the dealer creature's player NetId), then bare key.
    // Returns null if neither is registered in EntityLedger.
    internal static string? ResolveRelicLedgerKey(string powerId, Creature? dealer)
    {
        var dealerNetId = dealer?.Player?.NetId.ToString();
        if (dealerNetId != null)
        {
            var playerKey = $"RELIC_{powerId}_P{dealerNetId}";
            if (EntityLedger.ContainsKey(playerKey))
            {
                return playerKey;
            }
        }

        var bareKey = "RELIC_" + powerId;
        return EntityLedger.ContainsKey(bareKey) ? bareKey : null;
    }

    // relicScopedId is the output of GetRelicScopedId — either "{id}_P{netId}" or "{id}".
    // The ledger entry must already exist (created via GetOrCreateRelicStats at registration time).
    public static void AddRelicDamage(string relicScopedId, decimal amount)
    {
        if (amount <= 0) return;
        lock (SyncRoot)
        {
            var key = "RELIC_" + relicScopedId;
            if (EntityLedger.TryGetValue(key, out var entity) && entity is RelicStats stats)
            {
                stats.AddCombatDamage(amount, _currentAct, _currentCombatType);
                Log.Debug($"AddRelicDamage. Added {amount} to {key}");
            }
            else
            {
                Log.Warn($"AddRelicDamage. No ledger entry found for key: {key}");
            }
        }
        Publish();
    }

    public static void HandleRelicRemove(RelicModel relic, string? ownerNetId, int floorRemoved)
    {
        lock (SyncRoot)
        {
            var key = ownerNetId != null ? $"RELIC_{relic.Id.Entry}_P{ownerNetId}" : "RELIC_" + relic.Id.Entry;
            if (!EntityLedger.TryGetValue(key, out var entity) && ownerNetId != null)
            {
                // Fall back to bare key for relics registered before owner tracking was added.
                key = "RELIC_" + relic.Id.Entry;
                EntityLedger.TryGetValue(key, out entity);
            }

            if (entity != null)
            {
                entity.FloorRemoved = floorRemoved;
                entity.IsActive = false;
                Log.Debug($"HandleRelicRemove. Relic: {relic.Id.Entry}, Key: {key}, Floor: {floorRemoved}");
            }
            else
            {
                Log.Warn($"HandleRelicRemove. No entry found for Relic: {relic.Id.Entry}, OwnerNetId: {ownerNetId}");
            }
        }
        Publish();
    }

    // relicId must be the bare relic ID (e.g. "HAND_DRILL"), never a scoped ID.
    public static RelicStats GetOrCreateRelicStats(string relicId, string? ownerNetId = null)
    {
        var key = ownerNetId != null ? $"RELIC_{relicId}_P{ownerNetId}" : "RELIC_" + relicId;
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
