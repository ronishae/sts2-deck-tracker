namespace DeckTracker;

// Per-run record accumulated as the run progresses by RunLogRecorder. Persisted inside the run's save
// file so a resumed run keeps its combat history and the CSV high-water mark.
public sealed class RunLog
{
    public string RunSeed { get; set; } = "";
    // ISO-8601 UTC instant the run started. Disambiguates two attempts of the same seed in the master CSV.
    public string StartedAtUtc { get; set; } = "";
    public string Character { get; set; } = "";
    public int AscensionLevel { get; set; }
    // Slay the Spire 2 patch version the run was played on (e.g. "v0.107.0"), from ReleaseInfoManager.
    public string GameVersion { get; set; } = "";

    public List<CombatRecord> Combats { get; set; } = new();

    // High-water mark: Index of the last combat whose per-card rows were appended to the master
    // card_fights.csv. Persisted so a resume/reload never double-appends rows for already-exported combats.
    public int LastExportedCombatIndex { get; set; } = -1;
}
