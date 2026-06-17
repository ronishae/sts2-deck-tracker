namespace DeckTracker;

// Lightweight description of one master-deck card, used by RunLogRecorder.SyncDeck to diff the deck
// between rooms. BaseKey (CardStats.BaseCardKey) lets a version change be recognised as an upgrade/enchant
// rather than a separate remove + add.
public sealed class DeckCardInfo
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string BaseKey { get; set; } = "";
}
