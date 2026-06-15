using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void RenderFullScreenPotions()
    {
        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("POTION NAME", "NAME", 300));

        string combatColText = "COMBAT" + (_showRawForge ? " FORGE" : " DMG");
        string runColText = "RUN" + (_showRawForge ? " FORGE" : " DMG");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(runColText, "RUN_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(combatColText, "COMBAT_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("OBTAINED", "ADDED", 100));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("USED", "USED", 100));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("DISCARDED", "REMOVED", 100));

        // Effective = the displayed damage value, summing every damage bucket (direct + generated-card
        // damage from cards this potion created + connected-forge adjustment).
        decimal EffectiveCombat(PotionStats s) =>
            _showRawForge ? s.RawForgeCombat : (s.CombatDamage + s.GeneratedCombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0));

        decimal EffectiveRun(ActData agg) =>
            _showRawForge ? agg.RawForgeTotal : (agg.TotalDamage + agg.GeneratedDamageTotal + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0));

        var unsortedList = CardRegistry.EntityLedger.Values.OfType<PotionStats>()
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .ToList();

        var sortedList = _currentSort.Column switch
        {
            "NAME" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => GetEntityDisplayTitle(x.Stat))
                : unsortedList.OrderByDescending(x => GetEntityDisplayTitle(x.Stat)),

            "COMBAT_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveCombat(x.Stat))
                : unsortedList.OrderByDescending(x => EffectiveCombat(x.Stat)),

            "RUN_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveRun(x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveRun(x.Agg)),

            "ADDED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorObtained)
                : unsortedList.OrderByDescending(x => x.Stat.FloorObtained),

            "USED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorUsed)
                : unsortedList.OrderByDescending(x => x.Stat.FloorUsed),

            "REMOVED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorDiscarded)
                : unsortedList.OrderByDescending(x => x.Stat.FloorDiscarded),

            _ => unsortedList.OrderByDescending(x => EffectiveRun(x.Agg))
        };

        // Secondary sort to keep multiple instances ordered chronologically
        var potionList = sortedList.ThenBy(x => x.Stat.FloorObtained).ToList();

        foreach (var item in potionList)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };

            decimal valCombat = EffectiveCombat(stat);
            decimal valRun = EffectiveRun(agg);

            Label runDataLabel = new Label { Text = $"{valRun:0.##}", CustomMinimumSize = new Vector2(150, 0) };
            runDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && agg.ConnectedForgeTotal > 0 ? new Color("38BDF8") : (_showRawForge ? new Color("A0A8B4") : new Color("4ADE80")));

            Label combatDataLabel = new Label { Text = $"{valCombat:0.##}", CustomMinimumSize = new Vector2(150, 0) };
            combatDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && stat.ConnectedForgeCombat > 0 ? new Color("38BDF8") : (_showRawForge ? new Color("A0A8B4") : new Color("4ADE80")));

            string obtainedText = stat.FloorObtained < 0 ? "N/A" : stat.FloorObtained.ToString();
            Label obtainedLabel = new Label { Text = obtainedText, CustomMinimumSize = new Vector2(100, 0) };
            obtainedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            string usedText = stat.FloorUsed < 0 ? "N/A" : stat.FloorUsed.ToString();
            Label usedLabel = new Label { Text = usedText, CustomMinimumSize = new Vector2(100, 0) };
            usedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            string discardedText = stat.FloorDiscarded < 0 ? "N/A" : stat.FloorDiscarded.ToString();
            Label discardedLabel = new Label { Text = discardedText, CustomMinimumSize = new Vector2(100, 0) };
            discardedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            row.AddChild(nameLabel);
            row.AddChild(runDataLabel);
            row.AddChild(combatDataLabel);
            row.AddChild(obtainedLabel);
            row.AddChild(usedLabel);
            row.AddChild(discardedLabel);

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row));
        }
    }
}
