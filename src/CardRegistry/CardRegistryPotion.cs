using Godot;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{

    // We use a ConditionalWeakTable or Dictionary to map the live object to our unique ID
    // since the player can have 3 of the exact same potion!
    public static Dictionary<PotionModel, string> PotionInstanceIds = new();
    private static int _potionCounter = 0;

    // The resolved tracking id for the potion currently being used. Locked in at use-time so the
    // forge/damage that follow (which fire AFTER MarkPotionUsed) attribute to the same entry without
    // re-resolving against a stale active flag.
    private static readonly AsyncLocal<string?> _currentPlayingPotionId = new();
    public static string? CurrentPlayingPotionId => _currentPlayingPotionId.Value;

    public static void RegisterPotionProcured(PotionModel potion, int floor)
    {
        lock (SyncRoot)
        {
            _potionCounter++;
            string id = $"POTION_{potion.Id.Entry}_{_potionCounter}";
            PotionInstanceIds[potion] = id;

            // Note: Relies on localization being loaded, fallback to raw ID if not
            string displayName = potion.Title?.GetFormattedText() ?? potion.Id.Entry ?? "Unknown Potion";

            EntityLedger[id] = new PotionStats
            {
                Id = id,
                DisplayName = displayName,
                Model = potion,
                FloorObtained = floor,
                OwnerNetId = potion.Owner?.NetId.ToString(),
                IsActive = true
            };
            Log.Debug($"Procured Potion: {displayName} ({id}), Owner: {potion.Owner?.NetId.ToString() ?? "NONE"}");
        }

        ForcePublish();
    }

    // Resolves a potion model to its tracking id. The reference path is reliable for the local
    // player (same model instance from procure->use); remote players' models arrive as network
    // clones, so we fall back to matching the preserved owner NetId + potion type.
    public static bool TryResolvePotionId(PotionModel potion, out string id)
    {
        lock (SyncRoot)
        {
            if (PotionInstanceIds.TryGetValue(potion, out id!))
            {
                Log.VeryDebug($"TryResolvePotionId (reference). Potion: {potion.Id.Entry}, Id: {id}");
                return true;
            }

            var ownerNetId = potion.Owner?.NetId.ToString();
            var match = FindPotionByOwnerAndType(ownerNetId, potion.Id.Entry);
            if (match != null)
            {
                id = match.Id;
                Log.Debug($"TryResolvePotionId (owner+type). Potion: {potion.Id.Entry}, Owner: {ownerNetId ?? "NONE"}, Id: {id}");
                return true;
            }
        }

        Log.Warn($"TryResolvePotionId. Potion: {potion.Id.Entry}, Owner: {potion.Owner?.NetId.ToString() ?? "NONE"} could not be resolved.");
        id = string.Empty;
        return false;
    }

    // Picks the best registered PotionStats for an owner + potion type. Prefers a still-active entry,
    // then the most recently obtained, so forge/damage right after use still resolve to the just-used
    // potion. Must be called under SyncRoot.
    private static PotionStats? FindPotionByOwnerAndType(string? ownerNetId, string entry)
    {
        if (string.IsNullOrEmpty(ownerNetId))
        {
            return null;
        }

        var idPrefix = $"POTION_{entry}_";
        return EntityLedger.Values.OfType<PotionStats>()
            .Where(p => p.OwnerNetId == ownerNetId && MatchesPotionType(p.Id, idPrefix))
            .OrderByDescending(p => p.IsActive)
            .ThenByDescending(p => p.FloorObtained)
            .FirstOrDefault();
    }

    // True when id is exactly "POTION_{entry}_{counter}" with a numeric counter. Guards against an
    // entry that is a prefix of another (e.g. "KINGS" vs "KINGS_COURAGE") matching by accident.
    private static bool MatchesPotionType(string id, string idPrefix)
    {
        if (!id.StartsWith(idPrefix))
        {
            return false;
        }
        var counter = id.Substring(idPrefix.Length);
        return counter.Length > 0 && counter.All(char.IsDigit);
    }

    public static string? MarkPotionUsed(PotionModel potion, int floor)
    {
        string? id;
        lock (SyncRoot)
        {
            // Resolve while the entry is still active so the just-used potion wins over any other
            // identical active copy the player may hold.
            if (TryResolvePotionId(potion, out var resolved)
                && EntityLedger.TryGetValue(resolved, out var entity) && entity is PotionStats stat)
            {
                stat.FloorUsed = floor;
                stat.IsActive = false;
                id = resolved;
            }
            else
            {
                id = null;
            }
        }

        ForcePublish();
        return id;
    }

    public static void MarkPotionDiscarded(PotionModel potion, int floor)
    {
        lock (SyncRoot)
        {
            if (TryResolvePotionId(potion, out var id)
                && EntityLedger.TryGetValue(id, out var entity) && entity is PotionStats stat)
            {
                stat.FloorDiscarded = floor;
                stat.IsActive = false;
            }
        }

        ForcePublish();
    }

    public static void SetPlayingPotion(PotionModel? potion, string? resolvedId = null)
    {
        if (potion == null)
        {
            _currentPlayingPotionId.Value = null;
            return;
        }

        // Prefer an id already resolved by the caller (e.g. MarkPotionUsed); otherwise resolve now.
        _currentPlayingPotionId.Value = resolvedId
            ?? (TryResolvePotionId(potion, out var id) ? id : null);
    }
}
