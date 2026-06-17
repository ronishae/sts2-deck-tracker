namespace DeckTracker;

// Full per-run record accumulated as the run progresses by RunLogRecorder, then serialized to the
// user-facing export folder by RunExporter (and persisted inside the run's save file so a resumed run
// keeps its timeline). Everything analysable about a run lives here.
public sealed class RunLog
{
    public string RunSeed { get; set; } = "";
    // ISO-8601 UTC instant the run started. Disambiguates two attempts of the same seed in the master CSV.
    public string StartedAtUtc { get; set; } = "";
    public string Character { get; set; } = "";
    public int AscensionLevel { get; set; }

    // "InProgress" until the run resolves, then "Victory" or "Death".
    public string Outcome { get; set; } = "InProgress";
    public int FloorDied { get; set; } = -1;
    public string KilledBy { get; set; } = "";
    public int FinalFloor { get; set; }
    public int FinalGold { get; set; }

    public List<ActMapSnapshot> Maps { get; set; } = new();
    public List<PathStep> Path { get; set; } = new();
    public List<TimelineEvent> Timeline { get; set; } = new();
    public List<CombatRecord> Combats { get; set; } = new();
    public List<DeckChangeEvent> DeckChanges { get; set; } = new();

    // High-water mark: Index of the last combat whose per-card rows were appended to the master
    // card_fights.csv. Persisted so a resume/reload never double-appends rows for already-exported combats.
    public int LastExportedCombatIndex { get; set; } = -1;
}
