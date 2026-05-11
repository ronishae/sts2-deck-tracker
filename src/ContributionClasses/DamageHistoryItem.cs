using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class DamageHistoryItem(ICombatState combatState, Creature? dealer, DamageResult results,
    Creature target, CardModel? cardModel)
{
    public ICombatState CombatState { get; set; } = combatState;
    public Creature? Dealer { get; set; } = dealer;
    public DamageResult Results { get; set; } = results;
    public Creature Target { get; set; } = target;
    public CardModel? CardModel { get; set; } = cardModel;
}