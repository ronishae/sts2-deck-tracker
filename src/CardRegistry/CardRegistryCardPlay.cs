using MegaCrit.Sts2.Core.Models;

namespace DeckTracker;

public static partial class CardRegistry
{
    // Potion uses open the same draw-deferral window a card play does, so cards a potion creates — and
    // upgrades as part of the same action (e.g. Cunning Potion's upgraded Shivs) — are registered once the
    // potion fully resolves, at their final identity, instead of at their initial (un-upgraded) state.
    private static readonly AsyncLocal<bool> _potionUseDeferringDraws = new();

    public static CardModel? CurrentPlayingCard
    {
        get
        {
            return _currentPlayingCard.Value;
        }
    }

    public static void StartCardPlay(CardModel card)
    {
        _currentPlayingCard.Value = card;
        _deferredDraws.Value = new List<CardModel>();
        Log.Debug($"StartCardPlay. Card: {card.Id.Entry}");
    }

    public static void EndCardPlay()
    {
        Log.Debug("EndCardPlay.");
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _currentPlayingCard.Value = null;
    }

    public static bool IsCardPlayActive()
    {
        return _currentPlayingCard.Value != null;
    }

    public static void StartPotionUse()
    {
        if (_deferredDraws.Value != null)
        {
            return; // a card-play deferral is already open; don't clobber it
        }
        _deferredDraws.Value = new List<CardModel>();
        _potionUseDeferringDraws.Value = true;
    }

    public static void EndPotionUse()
    {
        if (!_potionUseDeferringDraws.Value)
        {
            return;
        }
        ProcessDeferredDraws();
        _deferredDraws.Value = null;
        _potionUseDeferringDraws.Value = false;
    }

    // True while any draw-deferral window is open (card play or potion use).
    public static bool IsDeferringDraws()
    {
        return _deferredDraws.Value != null;
    }

    public static void DeferDraw(CardModel card)
    {
        _deferredDraws.Value?.Add(card);
    }

    private static void ProcessDeferredDraws()
    {
        if (_deferredDraws.Value == null)
        {
            return;
        }

        foreach (var card in _deferredDraws.Value)
        {
            Log.Debug($"ProcessDeferredDraws. Registering deferred draw: {card.Id.Entry}");
            RegisterCard(card);
            AddDraw(card);
        }
        _deferredDraws.Value.Clear();
    }
}
