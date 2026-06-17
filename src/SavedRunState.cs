namespace DeckTracker;

public sealed class SavedRunState
{
    public string RunSeed { get; set; } = "";
    public int PotionCounter { get; set; }
    public Dictionary<string, CardStats> Totals { get; set; } = new();
    public Dictionary<string, PotionStats> Potions { get; set; } = new();
    public Dictionary<string, RelicStats> Relics { get; set; } = new();
    // Kept so older save files don't crash when deserializing
    public Dictionary<string, int> TypeCounters { get; set; } = new();
    // The run's event timeline / export log, persisted so a resumed run keeps its history and the master
    // CSV high-water mark. Null on saves made before this feature existed.
    public RunLog? RunLog { get; set; }
}
