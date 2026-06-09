using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void SetActiveTab(string tabName)
    {
        _activeTab = tabName;
        if (_cardsTabBtn != null) _cardsTabBtn.ButtonPressed = (tabName == "Cards");
        if (_relicsTabBtn != null) _relicsTabBtn.ButtonPressed = (tabName == "Relics");
        if (_potionsTabBtn != null) _potionsTabBtn.ButtonPressed = (tabName == "Potions");
        RedrawUI(_latestStats);
    }

    private static void ToggleMergeVersions()
    {
        _mergeCardVersions = !_mergeCardVersions;
        if (_mergeVersionsBtnLarge != null)
        {
            _mergeVersionsBtnLarge.Text = _mergeCardVersions ? "Merge Versions: ON" : "Merge Versions: OFF";
        }
        RedrawUI(_latestStats);
    }

    private static void ToggleHideZeroDamage()
    {
        _hideZeroDamageCards = !_hideZeroDamageCards;
        if (_hideZeroDamageBtnLarge != null)
        {
            _hideZeroDamageBtnLarge.Text = _hideZeroDamageCards ? "Hide 0 Damage: ON" : "Hide 0 Damage: OFF";
        }
        RedrawUI(_latestStats);
    }

    private static void ToggleRawForge()
    {
        _showRawForge = !_showRawForge;
        if (_toggleRawForgeBtnLarge != null) _toggleRawForgeBtnLarge.Text = _showRawForge ? "Show Raw Forge: ON" : "Show Raw Forge: OFF";
        RedrawUI(_latestStats);
    }

    private static void ToggleForgeDamage()
    {
        _includeConnectedForge = !_includeConnectedForge;
        if (_toggleForgeDmgBtnSmall != null) _toggleForgeDmgBtnSmall.Text = _includeConnectedForge ? "+Forge: ON" : "+Forge: OFF";
        if (_toggleForgeDmgBtnLarge != null) _toggleForgeDmgBtnLarge.Text = _includeConnectedForge ? "Include Connected Forge: ON" : "Include Connected Forge: OFF";
        RedrawUI(_latestStats);
    }

    private static void OnTogglePressed()
    {
        _showRunStats = !_showRunStats;
        if (_titleLabel != null) _titleLabel.Text = _showRunStats ? "Tracker (Run)" : "Tracker (Combat)";
        if (_toggleBtn != null) _toggleBtn.Text = _showRunStats ? "Combat" : "Run";
        if (_toggleRunCombatBtnLarge != null) _toggleRunCombatBtnLarge.Text = _showRunStats ? "Show Combat Stats" : "Show Run Stats";
        RedrawUI(_latestStats);
    }

    private static void OnExpandPressed() => SetFullScreenVisible(true);
    private static void OnClosePressed() => SetFullScreenVisible(false);
}
