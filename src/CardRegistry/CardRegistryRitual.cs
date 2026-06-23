namespace DeckTracker;

public static partial class CardRegistry
{
    public static Dictionary<string, decimal> RitualSources = new();
    public static bool IsRitualTriggering;

    public static void ResetRitualState()
    {
        RitualSources.Clear();
        IsRitualTriggering = false;
    }
}