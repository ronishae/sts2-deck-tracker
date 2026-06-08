namespace DeckTracker;

public sealed class CardStats : EntityStats
{
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int CopiesInDeck { get; set; }
    public int UpgradeLevel { get; set; }
    // Shared key across all upgrade/enchant versions of the same physical card: "{baseId}_F{floorAdded}"
    public string BaseCardKey { get; set; } = "";

    public override EntityStats Clone()
    {
        var clone = new CardStats
        {
            CardType = CardType,
            Enchantment = Enchantment,
            CopiesInDeck = CopiesInDeck,
            UpgradeLevel = UpgradeLevel,
            BaseCardKey = BaseCardKey,
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
        CopyBaseFields(clone);
        return clone;
    }
}