namespace DeckTracker;

// A change to the master deck (card added, removed, upgraded, or enchanted) with the floor it happened on
// and the inferred source (the room type at the time). Built by diffing the out-of-combat deck between rooms.
public sealed class DeckChangeEvent
{
    public int Floor { get; set; }
    // "Added" | "Removed" | "Upgraded" | "Enchanted".
    public string ChangeType { get; set; } = "";
    public string CardId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    // "Reward" | "Shop" | "Event" | "Rest" | "Unknown".
    public string Source { get; set; } = "";
}
