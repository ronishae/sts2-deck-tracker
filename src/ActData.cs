namespace DeckTracker;

public class ActData
{
    public int TimesDrawn { get; set; }
    public int TimesPlayed { get; set; }
    public decimal PlayRate => TimesDrawn > 0 ? (decimal)TimesPlayed / TimesDrawn : 0;

    public decimal DamageHallway { get; set; }
    public decimal DamageElite { get; set; }
    public decimal DamageBoss { get; set; }
    public decimal TotalDamage => DamageHallway + DamageElite + DamageBoss;
    
    public decimal RawForgeHallway { get; set; }
    public decimal RawForgeElite { get; set; }
    public decimal RawForgeBoss { get; set; }
    public decimal RawForgeTotal => RawForgeHallway + RawForgeElite + RawForgeBoss;
    
    public decimal ConnectedForgeHallway { get; set; }
    public decimal ConnectedForgeElite { get; set; }
    public decimal ConnectedForgeBoss { get; set; }
    public decimal ConnectedForgeTotal => ConnectedForgeHallway + ConnectedForgeElite + ConnectedForgeBoss;
    
    public decimal ReceivedForgeHallway { get; set; }
    public decimal ReceivedForgeElite { get; set; }
    public decimal ReceivedForgeBoss { get; set; }
    public decimal ReceivedForgeTotal => ReceivedForgeHallway + ReceivedForgeElite + ReceivedForgeBoss;

    public int EncountersSeenHallway { get; set; }
    public int EncountersSeenElite { get; set; }
    public int EncountersSeenBoss { get; set; }
    public int EncountersSeenTotal => EncountersSeenHallway + EncountersSeenElite + EncountersSeenBoss;

    public decimal AvgHallway => EncountersSeenHallway > 0 ? DamageHallway / EncountersSeenHallway : 0;
    public decimal AvgElite => EncountersSeenElite > 0 ? DamageElite / EncountersSeenElite : 0;
    public decimal AvgBoss => EncountersSeenBoss > 0 ? DamageBoss / EncountersSeenBoss : 0;
    public decimal AvgTotal => EncountersSeenTotal > 0 ? TotalDamage / EncountersSeenTotal : 0;

    public void AddDamage(string combatType, decimal amount)
    {
        switch (combatType)
        {
            case "Elite": DamageElite += amount; break;
            case "Boss": DamageBoss += amount; break;
            default: DamageHallway += amount; break;
        }
    }

    public void AddEncounterSeen(string combatType)
    {
        switch (combatType)
        {
            case "Elite": EncountersSeenElite++; break;
            case "Boss": EncountersSeenBoss++; break;
            default: EncountersSeenHallway++; break;
        }
    }

    public void AddRawForge(string combatType, decimal amount)
    {
        switch (combatType)
        {
            case "Elite": RawForgeElite += amount; break;
            case "Boss": RawForgeBoss += amount; break;
            default: RawForgeHallway += amount; break;
        }
    }

    public void AddConnectedForge(string combatType, decimal amount)
    {
        switch (combatType)
        {
            case "Elite": ConnectedForgeElite += amount; break;
            case "Boss": ConnectedForgeBoss += amount; break;
            default: ConnectedForgeHallway += amount; break;
        }
    }

    public void AddReceivedForge(string combatType, decimal amount)
    {
        switch (combatType)
        {
            case "Elite": ReceivedForgeElite += amount; break;
            case "Boss": ReceivedForgeBoss += amount; break;
            default: ReceivedForgeHallway += amount; break;
        }
    }

    public ActData Clone()
    {
        return new ActData
        {
            TimesDrawn = TimesDrawn,
            TimesPlayed = TimesPlayed,
            DamageHallway = DamageHallway,
            DamageElite = DamageElite,
            DamageBoss = DamageBoss,
            RawForgeHallway = RawForgeHallway,
            RawForgeElite = RawForgeElite,
            RawForgeBoss = RawForgeBoss,
            ConnectedForgeHallway = ConnectedForgeHallway,
            ConnectedForgeElite = ConnectedForgeElite,
            ConnectedForgeBoss = ConnectedForgeBoss,
            ReceivedForgeHallway = ReceivedForgeHallway,
            ReceivedForgeElite = ReceivedForgeElite,
            ReceivedForgeBoss = ReceivedForgeBoss,
            EncountersSeenHallway = EncountersSeenHallway,
            EncountersSeenElite = EncountersSeenElite,
            EncountersSeenBoss = EncountersSeenBoss
        };
    }
}
