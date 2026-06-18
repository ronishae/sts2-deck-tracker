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

        var unsortedList = CardRegistry.EntityLedger.Values.OfType<PotionStats>()
            .Where(s => _enabledPlayers.Contains(s.PlayerIndex))
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .Where(x => !_hideZeroDamageCards || EffectiveCombat(x.Stat) > 0 || EffectiveRun(x.Agg) > 0)
            .ToList();

        var sortedList = _currentSort.Column switch
        {
            "NAME"       => SortBy(unsortedList, x => GetEntityDisplayTitle(x.Stat)),
            "COMBAT_DMG" => SortBy(unsortedList, x => EffectiveCombat(x.Stat)),
            "RUN_DMG"    => SortBy(unsortedList, x => EffectiveRun(x.Agg)),
            "ADDED"      => SortBy(unsortedList, x => x.Stat.FloorObtained),
            "USED"       => SortBy(unsortedList, x => x.Stat.FloorUsed),
            "REMOVED"    => SortBy(unsortedList, x => x.Stat.FloorDiscarded),
            _            => unsortedList.OrderByDescending(x => EffectiveRun(x.Agg))
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

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row, GetPlayerRowBgColor(stat.PlayerIndex)));
        }
    }
}
