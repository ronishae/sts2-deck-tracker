namespace DeckTracker;

public sealed class CardStats : EntityStats
{
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int CopiesInDeck { get; set; }
    public int UpgradeLevel { get; set; }
    // Shared key across all upgrade/enchant versions of the same card for one owner: "{baseId}_F{floorAdded}_P{ownerNetId}"
    public string BaseCardKey { get; set; } = "";
    // Index into the ordered player list — used to colour-code and filter cards per player in the overlay
    public int PlayerIndex { get; set; }
    // Tracking id of the source (card/potion/relic) that generated this card mid-combat, when it was made
    // by a known generator. Empty for deck cards and for generated cards with no resolvable creator. Used
    // by the overlay to nest generated cards under their creator, and persisted for later history export.
    public string GeneratedById { get; set; } = "";

    public override EntityStats Clone()
    {
        var clone = new CardStats
        {
            CardType = CardType,
            Enchantment = Enchantment,
            CopiesInDeck = CopiesInDeck,
            UpgradeLevel = UpgradeLevel,
            BaseCardKey = BaseCardKey,
            PlayerIndex = PlayerIndex,
            GeneratedById = GeneratedById,
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
        CopyBaseFields(clone);
        return clone;
    }
}