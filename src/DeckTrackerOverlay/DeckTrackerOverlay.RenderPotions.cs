using Godot;
using System.Linq;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void RenderFullScreenPotions()
    {
        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("POTION NAME", "NAME", 300));

        string totalColText = (_showRunStats ? "RUN" : "COMBAT") + (_showRawForge ? " FORGE" : " DAMAGE");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(totalColText, "TOTAL_DMG", 180));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("OBTAINED", "ADDED", 100));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("USED", "USED", 100));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("DISCARDED", "REMOVED", 100));

        decimal EffectiveValue(PotionStats s, ActData agg) =>
            _showRunStats
                ? (_showRawForge ? agg.RawForgeTotal : (agg.TotalDamage + (_includeConnectedForge ? agg.ConnectedForgeTotal - agg.ReceivedForgeTotal : 0)))
                : (_showRawForge ? s.RawForgeCombat : (s.CombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0)));

        var unsortedList = CardRegistry.EntityLedger.Values.OfType<PotionStats>()
            .Select(s => new { Stat = s, Agg = AggregateActData(s) })
            .ToList();

        var sortedList = _currentSort.Column switch
        {
            "NAME" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => GetEntityDisplayTitle(x.Stat))
                : unsortedList.OrderByDescending(x => GetEntityDisplayTitle(x.Stat)),

            "TOTAL_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveValue(x.Stat, x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveValue(x.Stat, x.Agg)),

            "ADDED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorObtained)
                : unsortedList.OrderByDescending(x => x.Stat.FloorObtained),

            "USED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorUsed)
                : unsortedList.OrderByDescending(x => x.Stat.FloorUsed),

            "REMOVED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorDiscarded)
                : unsortedList.OrderByDescending(x => x.Stat.FloorDiscarded),

            _ => unsortedList.OrderByDescending(x => EffectiveValue(x.Stat, x.Agg))
        };

        // Secondary sort to keep multiple instances ordered chronologically
        var potionList = sortedList.ThenBy(x => x.Stat.FloorObtained).ToList();

        foreach (var item in potionList)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };

            decimal valToShow = EffectiveValue(stat, agg);
            Label totalDataLabel = new Label { Text = $"{valToShow:0.##}", CustomMinimumSize = new Vector2(180, 0) };
            if (!_showRawForge && _includeConnectedForge)
            {
                bool hasForge = _showRunStats ? agg.ConnectedForgeTotal > 0 : stat.ConnectedForgeCombat > 0;
                totalDataLabel.AddThemeColorOverride("font_color", hasForge ? new Color("38BDF8") : new Color("4ADE80"));
            }
            else
            {
                totalDataLabel.AddThemeColorOverride("font_color", _showRawForge ? new Color("A0A8B4") : new Color("4ADE80"));
            }

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
            row.AddChild(totalDataLabel);
            row.AddChild(obtainedLabel);
            row.AddChild(usedLabel);
            row.AddChild(discardedLabel);

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row));
        }
    }
}
