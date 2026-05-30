namespace DeckTracker;

public partial class CardRegistry
{
    // --- SPINNER TRACKING ---
    public static readonly AsyncLocal<bool> IsSpinnerExecuting = new();
    public static int SpinnerExecutionIndex;
    public static List<string> SpinnerSources = new();
}