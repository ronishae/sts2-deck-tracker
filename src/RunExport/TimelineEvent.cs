namespace DeckTracker;

// One ordered milestone in the run. EventType is one of the RunLogRecorder.Event* string constants.
// The optional fields carry only what that event needs: a human label, a free-form detail string, a
// numeric amount (gold, hp, ...), and/or a link to the CombatRecord this event refers to.
public sealed class TimelineEvent
{
    public string EventType { get; set; } = "";
    public int Floor { get; set; }
    public int Act { get; set; }
    public string? Label { get; set; }
    public string? Detail { get; set; }
    public decimal? Amount { get; set; }
    public int? CombatIndex { get; set; }
}
