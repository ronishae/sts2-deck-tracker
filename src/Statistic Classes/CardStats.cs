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
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
        CopyBaseFields(clone);
        return clone;
    }
}