using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static class DeckDamageService
{
    public static void RecordDamage(CardModel card, decimal damage)
    {
        if (damage <= 0) return;
        
        // 1. Ensure it exists in the registry
        CardRegistry.RegisterCard(card);

        // 2. Add the damage values
        CardRegistry.AddDamage(card, damage);
    }
}