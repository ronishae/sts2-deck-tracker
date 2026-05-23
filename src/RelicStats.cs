namespace DeckTracker;
public sealed class RelicStats : EntityStats
{
    public string Rarity { get; set; } = ""; // e.g., Starter, Common, Boss
    
    // Relics often trigger a specific number of times (e.g. Incense Burner popped 5 times)
    public int CombatTriggers { get; set; } 
    public int RunTriggers { get; set; }

    // Catch-all for weird relic stats (Healing, Gold, Block, Energy)
    public Dictionary<string, decimal> CustomMetrics { get; set; } = new();

    public override EntityStats Clone()
    {
        var clone = new RelicStats
        {
            Rarity = Rarity,
            CombatTriggers = CombatTriggers,
            RunTriggers = RunTriggers,
            CustomMetrics = new Dictionary<string, decimal>(CustomMetrics) // Shallow copy is fine for decimals
        };
        CopyBaseFields(clone);
        return clone;
    }
}