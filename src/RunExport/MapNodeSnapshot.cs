namespace DeckTracker;

// A single map node: its grid position, room type, and the grid positions of its child nodes (the edges
// leaving it). Each child is stored as a two-element [col, row] array to keep the JSON compact.
public sealed class MapNodeSnapshot
{
    public int Col { get; set; }
    public int Row { get; set; }
    public string PointType { get; set; } = "";
    public List<int[]> Children { get; set; } = new();
}
