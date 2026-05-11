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
    
    public int TimesDrawn { get; set; }
    public int TimesPlayed { get; set; }
    public decimal PlayRate => TimesDrawn > 0 ? (decimal)TimesPlayed / TimesDrawn : 0;
    
    public decimal CombatDamage { get; set; }
    public decimal RunDamage { get; set; }
    public decimal DamageHallway { get; set; }
    public decimal DamageElite { get; set; }
    public decimal DamageBoss { get; set; }
    
    public decimal RawForgeTotal { get; set; }
    public decimal RawForgeCombat { get; set; }
    public decimal RawForgeHallway { get; set; }
    public decimal RawForgeElite { get; set; }
    public decimal RawForgeBoss { get; set; }
    public decimal ConnectedForgeCombat { get; set; }
    public decimal ConnectedForgeTotal { get; set; }
    public decimal ConnectedForgeHallway { get; set; }
    public decimal ConnectedForgeElite { get; set; }
    public decimal ConnectedForgeBoss { get; set; }
    
    public decimal ReceivedForgeCombat { get; set; }
    public decimal ReceivedForgeTotal { get; set; }
    public decimal ReceivedForgeHallway { get; set; }
    public decimal ReceivedForgeElite { get; set; }
    public decimal ReceivedForgeBoss { get; set; }

    public int EncountersSeenTotal { get; set; }
    public int EncountersSeenHallway { get; set; }
    public int EncountersSeenElite { get; set; }
    public int EncountersSeenBoss { get; set; }

    public decimal AvgTotal => EncountersSeenTotal > 0 ? RunDamage / EncountersSeenTotal : 0;
    public decimal AvgHallway => EncountersSeenHallway > 0 ? DamageHallway / EncountersSeenHallway : 0;
    public decimal AvgElite => EncountersSeenElite > 0 ? DamageElite / EncountersSeenElite : 0;
    public decimal AvgBoss => EncountersSeenBoss > 0 ? DamageBoss / EncountersSeenBoss : 0;

    public CardStats Clone()
    {
        return new CardStats
        {
            CardId = CardId, DisplayName = DisplayName, CardType = CardType, FloorAdded = FloorAdded, 
            Enchantment = Enchantment,
            FloorRemoved = FloorRemoved, FloorLeftDeck = FloorLeftDeck, IsInDeck = IsInDeck, CopiesInDeck = CopiesInDeck,
            TimesDrawn = TimesDrawn, TimesPlayed = TimesPlayed,
            CombatDamage = CombatDamage, RunDamage = RunDamage,
            DamageHallway = DamageHallway, DamageElite = DamageElite, DamageBoss = DamageBoss,
            RawForgeTotal = RawForgeTotal, RawForgeCombat = RawForgeCombat, RawForgeHallway = RawForgeHallway,
            RawForgeElite = RawForgeElite, RawForgeBoss = RawForgeBoss,
            ConnectedForgeCombat = ConnectedForgeCombat, ConnectedForgeTotal = ConnectedForgeTotal,
            ConnectedForgeHallway = ConnectedForgeHallway, ConnectedForgeElite = ConnectedForgeElite, ConnectedForgeBoss = ConnectedForgeBoss,
            ReceivedForgeCombat = ReceivedForgeCombat, ReceivedForgeTotal = ReceivedForgeTotal,
            ReceivedForgeHallway = ReceivedForgeHallway, ReceivedForgeElite = ReceivedForgeElite, ReceivedForgeBoss = ReceivedForgeBoss,
            EncountersSeenTotal = EncountersSeenTotal, EncountersSeenHallway = EncountersSeenHallway,
            EncountersSeenElite = EncountersSeenElite, EncountersSeenBoss = EncountersSeenBoss
        };
    }
}
