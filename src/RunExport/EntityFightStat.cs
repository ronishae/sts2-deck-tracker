namespace DeckTracker;

// One entity's (card, relic, or potion) stats for a single combat. These are the source rows for the
// master card_fights.csv export and the per-combat Contributions in the JSON. Type-specific fields are
// nullable so they render blank in the CSV for the entity types they don't apply to.
public sealed class EntityFightStat
{
    public string EntityType { get; set; } = ""; // Card | Relic | Potion
    public string Name { get; set; } = "";
    public int PlayerIndex { get; set; }
    public string OwnerNetId { get; set; } = "";

    // Card identity, split out of the tracking id (letters stripped to numbers). Blank for relics/potions
    // except FloorAdded, which also applies to relics.
    public int? FloorAdded { get; set; }
    public int? CopyIndex { get; set; }
    public int? UpgradeLevel { get; set; }
    public string Enchantment { get; set; } = "";

    // Relic-only.
    public string Rarity { get; set; } = "";

    // Potion-only.
    public int? FloorObtained { get; set; }
    public int? FloorUsed { get; set; }
    public int? FloorDiscarded { get; set; }

    // Card-only activity.
    public int? TimesDrawn { get; set; }
    public int? TimesPlayed { get; set; }
    public decimal? PlayRate { get; set; }

    // Shared contribution metrics.
    public decimal Damage { get; set; }
    public decimal GeneratedDamage { get; set; }
    // This entity's Damage as a share of all entities' damage that fight (0..100).
    public decimal DamageContribPct { get; set; }
    public decimal RawForge { get; set; }
    public decimal ConnectedForge { get; set; }
    public decimal ReceivedForge { get; set; }
}
