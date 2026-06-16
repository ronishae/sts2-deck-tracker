using MegaCrit.Sts2.Core.Entities.Creatures;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Cards whose forced Block removal (CreatureCmd.LoseBlock) is credited as damage. Expand by adding ids;
    // to make it fully general, credit unconditionally instead of gating on this set.
    private static readonly HashSet<string> BlockRemovalAttributedCardIds = new() { "EXPOSE" };

    // Credits the currently-played card for enemy Block it forcibly strips (e.g. Expose), treating removed
    // Block as damage. Block absorbed by normal attacks does NOT reach here (it goes through
    // DamageBlockInternal), and natural end-of-turn falloff uses ClearBlock, so neither is double-counted.
    // Must be called from the LoseBlock prefix so target.Block is read before it is reduced.
    public static void HandleForcedBlockLoss(Creature target, decimal amount)
    {
        var card = CurrentPlayingCard;
        if (card == null || target.IsPlayer)
        {
            return; // not during a card play, or player's own block — not attributed
        }

        var blockLost = Math.Min(amount, (decimal)target.Block);
        if (blockLost <= 0)
        {
            return;
        }

        var cardId = card.Id.Entry ?? "";
        if (!BlockRemovalAttributedCardIds.Contains(cardId))
        {
            Log.Warn($"HandleForcedBlockLoss. Untracked forced block removal by card: {cardId}, BlockLost: {blockLost}, Target: {target.Name}. Add its id to BlockRemovalAttributedCardIds to credit it.");
            return;
        }

        Log.Debug($"HandleForcedBlockLoss. Card: {cardId}, BlockLost: {blockLost}, Target: {target.Name}");
        AddDamage(card, blockLost);
    }
}
