using Godot;

namespace DeckTracker;

// Central logging wrapper for the mod. Call sites pass only the message; the [DeckTracker][Level]
// prefix is added here so it stays consistent and grep-able.
public static class Log
{
    // Manual log-level switch. Ships at Info so normal play stays quiet (the per-event Debug/VeryDebug
    // flood is the main per-turn cost); press J in-game to toggle VeryDebug back on when capturing a
    // bug report. Lower it further locally during development if even Info gets noisy.
    public static LogLevel Level = LogLevel.Info;

    public static void VeryDebug(string message) => Write(LogLevel.VeryDebug, message);

    public static void Debug(string message) => Write(LogLevel.Debug, message);

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warn, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    private static void Write(LogLevel level, string message)
    {
        if (level < Level)
        {
            return;
        }

        var line = $"[DeckTracker][{level}] {message}";

        // Warn and Error go to the error stream so they surface in red and in crash logs.
        if (level >= LogLevel.Warn)
        {
            GD.PrintErr(line);
            return;
        }

        GD.Print(line);
    }
}
