namespace DeckTracker;

public sealed class CardStats : EntityStats
{
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int CopiesInDeck { get; set; }
    
    // Forge specifics
    public decimal RawForgeCombat { get; set; }
    public decimal ConnectedForgeCombat { get; set; }
    public decimal ReceivedForgeCombat { get; set; }

    public override EntityStats Clone()
    {
        var clone = new CardStats
        {
            CardType = CardType,
            Enchantment = Enchantment,
            CopiesInDeck = CopiesInDeck,
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
        CopyBaseFields(clone);
        return clone;
    }
}