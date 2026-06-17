using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class FurnaceContribution
{
    public CardModel CardSource { get; init; } = null!;
    public decimal PowerAmount { get; init; }
}
