using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public class DamageHistoryItem(ICombatState combatState, Creature? dealer, DamageResult results,
    Creature target, CardModel? cardModel, decimal baseDamage)
{
    public ICombatState CombatState { get; set; } = combatState;
    public Creature? Dealer { get; set; } = dealer;
    public DamageResult Results { get; set; } = results;
    public Creature Target { get; set; } = target;
    public CardModel? CardModel { get; set; } = cardModel;
    // The card-intrinsic damage after ProcessDamageSnapshot has peeled off and paid out every modifier
    // (Strength/Vigor additives, Vulnerable multipliers) to their own sources. This — not Results.TotalDamage
    // (the full dealt amount) — is what the blade and its forgers should split, or those modifiers would be
    // double-counted. Equals Results.TotalDamage when the hit had no tracked modifiers.
    public decimal BaseDamage { get; set; } = baseDamage;
}