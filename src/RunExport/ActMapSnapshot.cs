namespace DeckTracker;

// One act's map graph, flattened from the game's ActMap when it is generated, so the full node layout
// and its connections can be reconstructed offline.
public sealed class ActMapSnapshot
{
    public int ActIndex { get; set; }
    public List<MapNodeSnapshot> Nodes { get; set; } = new();
}
