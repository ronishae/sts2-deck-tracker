namespace DeckTracker;

public partial class CardRegistry
{
    public static readonly AsyncLocal<bool> IsLightningRodExecuting = new();
    public static Queue<string> LightningRodQueue = new();
}