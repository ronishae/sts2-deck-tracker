namespace DeckTracker;

// Ordered by ascending severity so a single >= comparison against the active level decides
// whether a message is shown. Mirrors the game's own LogLevel ordering minus the unused Load tier.
public enum LogLevel
{
    VeryDebug,
    Debug,
    Info,
    Warn,
    Error
}
