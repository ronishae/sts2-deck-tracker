namespace DeckTracker;

public sealed class CardStats : EntityStats
{
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int CopiesInDeck { get; set; }
    public int UpgradeLevel { get; set; }
    // Shared key across all upgrade/enchant versions of the same card for one owner: "{baseId}_F{floorAdded}_P{ownerNetId}"
    public string BaseCardKey { get; set; } = "";
    // Tracking id of the ROOT source (card/potion/relic) that generated this card mid-combat, when it was
    // made by a known generator. Empty for deck cards and for generated cards with no resolvable creator.
    // Used for damage routing (the root's generated bucket) and persisted for later history export.
    public string GeneratedById { get; set; } = "";
    // Tracking id of the IMMEDIATE generator (one step up the chain, before rolling up to the root). For a
    // direct generation this equals GeneratedById; for a chain (Spectrum Shift -> Discovery -> Noxious Fumes)
    // it is the parent that actually created this card. Used by the overlay to build the multi-level
    // generation tree; persisted alongside GeneratedById.
    public string GeneratedByImmediateId { get; set; } = "";

    public override EntityStats Clone()
    {
        var clone = new CardStats
        {
            CardType = CardType,
            Enchantment = Enchantment,
            CopiesInDeck = CopiesInDeck,
            UpgradeLevel = UpgradeLevel,
            BaseCardKey = BaseCardKey,
            GeneratedById = GeneratedById,
            GeneratedByImmediateId = GeneratedByImmediateId,
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
        CopyBaseFields(clone);
        return clone;
    }
}