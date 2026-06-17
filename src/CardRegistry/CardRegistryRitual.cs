namespace DeckTracker;

public static partial class CardRegistry
{
    public static Dictionary<string, decimal> RitualSources = new();
    public static readonly AsyncLocal<bool> IsRitualTriggering = new();

    public static void ResetRitualState()
    {
        RitualSources.Clear();
        IsRitualTriggering.Value = false;
    }
}