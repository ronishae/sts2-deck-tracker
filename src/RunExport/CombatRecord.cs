namespace DeckTracker;

// Per-individual-combat record: where it happened, how it went, and each card's contribution. This is
// the data the per-act aggregates can never recover, so it is captured at combat end before reset.
public sealed class CombatRecord
{
    public int Index { get; set; }
    public int Floor { get; set; }
    public int Act { get; set; }
    // The specific act variant (e.g. OVERGROWTH vs UNDERDOCKS for act 1), distinct from the act number.
    public string ActName { get; set; } = "";
    public string CombatType { get; set; } = "";
    public string EncounterId { get; set; } = "";
    public int Turns { get; set; }
    public decimal DamageTaken { get; set; }
    public decimal BlockGained { get; set; }
    public int PlayerHpBefore { get; set; }
    public int PlayerHpAfter { get; set; }
    // Gold entering the fight. Gold only changes between combats (rewards land after), so a single
    // "before" value is all that is meaningful; the GoldGained timeline events carry the actual deltas.
    public int GoldBefore { get; set; }
    // "Won" or "Died".
    public string Outcome { get; set; } = "";
    public List<EntityFightStat> Contributions { get; set; } = new();
}
