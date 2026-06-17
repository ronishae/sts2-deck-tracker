namespace DeckTracker;

public static partial class CardRegistry
{
    public static readonly AsyncLocal<int> PendingBootDamage = new();
}