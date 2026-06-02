namespace DeckTracker;

public static partial class CardRegistry
{
    // --- THE BOOT TRACKING ---
    public static readonly AsyncLocal<int> PendingBootDamage = new();
}