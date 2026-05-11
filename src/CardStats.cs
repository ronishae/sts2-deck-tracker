namespace DeckTracker;

public sealed class CardStats
{
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string CardType { get; set; } = "";
    public string Enchantment { get; set; } = "";
    public int FloorAdded { get; set; }
    public int FloorRemoved { get; set; } = -1;
    public int FloorLeftDeck { get; set; } = -1;
    public bool IsInDeck { get; set; } = true;
    public int CopiesInDeck { get; set; }
    
    public ActData Act1 { get; set; } = new();
    public ActData Act2 { get; set; } = new();
    public ActData Act3 { get; set; } = new();
    public ActData Act4 { get; set; } = new();
    
    // Combat-only metrics (cleared every fight)
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }
    public decimal RawForgeCombat { get; set; }
    public decimal ConnectedForgeCombat { get; set; }
    public decimal ReceivedForgeCombat { get; set; }

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId, 
            DisplayName = DisplayName, 
            CardType = CardType, 
            Enchantment = Enchantment,
            FloorAdded = FloorAdded, 
            FloorRemoved = FloorRemoved, 
            FloorLeftDeck = FloorLeftDeck, 
            IsInDeck = IsInDeck, 
            CopiesInDeck = CopiesInDeck,
            Act1 = Act1.Clone(),
            Act2 = Act2.Clone(),
            Act3 = Act3.Clone(),
            Act4 = Act4.Clone(),
            CombatDamage = CombatDamage,
            RunDamage = RunDamage,
            RawForgeCombat = RawForgeCombat,
            ConnectedForgeCombat = ConnectedForgeCombat,
            ReceivedForgeCombat = ReceivedForgeCombat
        };
    }
}
