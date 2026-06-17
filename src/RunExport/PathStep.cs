namespace DeckTracker;

// One step on the route the player actually walked, captured on room entry from the current map point.
public sealed class PathStep
{
    public int Act { get; set; }
    public int Floor { get; set; }
    public int Col { get; set; }
    public int Row { get; set; }
    public string PointType { get; set; } = "";
}
