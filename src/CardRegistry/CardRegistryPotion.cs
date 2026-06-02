using System.Text.RegularExpressions;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{

    // We use a ConditionalWeakTable or Dictionary to map the live object to our unique ID
    // since the player can have 3 of the exact same potion!
    public static Dictionary<PotionModel, string> PotionInstanceIds = new();
    private static int _potionCounter = 0;

    private static readonly AsyncLocal<PotionModel?> _currentPlayingPotion = new();
    public static PotionModel? CurrentPlayingPotion => _currentPlayingPotion.Value;

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
                IsActive = true
            };
            GD.Print($"[DeckTracker] Procured Potion: {displayName} ({id})");
        }

        ForcePublish();
    }

    public static void MarkPotionUsed(PotionModel potion, int floor)
    {
        lock (SyncRoot)
        {
            if (PotionInstanceIds.TryGetValue(potion, out var id)
                && EntityLedger.TryGetValue(id, out var entity) && entity is PotionStats stat)
            {
                stat.FloorUsed = floor;
                stat.IsActive = false;
            }
        }

        ForcePublish();
    }

    public static void MarkPotionDiscarded(PotionModel potion, int floor)
    {
        lock (SyncRoot)
        {
            if (PotionInstanceIds.TryGetValue(potion, out var id)
                && EntityLedger.TryGetValue(id, out var entity) && entity is PotionStats stat)
            {
                stat.FloorDiscarded = floor;
                stat.IsActive = false;
            }
        }

        ForcePublish();
    }

    public static void SetPlayingPotion(PotionModel? potion)
    {
        _currentPlayingPotion.Value = potion;
    }
}