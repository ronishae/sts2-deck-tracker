using Godot;

namespace DeckTracker;

public static partial class DeckTrackerOverlay
{
    private static void RenderFullScreenRelics()
    {
        _fullScreenHeadersContainer!.AddChild(CreateSortableHeader("RELIC NAME", "NAME", 300));

        string combatColText = "COMBAT" + (_showRawForge ? " FORGE" : " DMG");
        string runColText = "RUN" + (_showRawForge ? " FORGE" : " DMG");
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(runColText, "RUN_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader(combatColText, "COMBAT_DMG", 150));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("AVG (#Fights)", "AVG_DMG", 130));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("HALLWAY (AVG) (#)", "HALLWAY_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ELITE (AVG) (#)", "ELITE_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("BOSS (AVG) (#)", "BOSS_DMG", 185));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("ADDED", "ADDED", 80));
        _fullScreenHeadersContainer.AddChild(CreateSortableHeader("REMOVED", "REMOVED", 90));

        // Effective = the displayed damage value, summing every damage bucket (direct + generated-card
        // damage from cards this relic created + connected-forge adjustment).
        decimal EffectiveCombat(RelicStats s) =>
            _showRawForge ? s.RawForgeCombat : (s.CombatDamage + s.GeneratedCombatDamage + (_includeConnectedForge ? s.ConnectedForgeCombat - s.ReceivedForgeCombat : 0));

        decimal EffectiveRun(ActData a) =>
            _showRawForge ? a.RawForgeTotal : (a.TotalDamage + a.GeneratedDamageTotal + (_includeConnectedForge ? a.ConnectedForgeTotal - a.ReceivedForgeTotal : 0));

        decimal EffectiveHallway(ActData a) =>
            _showRawForge ? a.RawForgeHallway : (a.DamageHallway + a.GeneratedDamageHallway + (_includeConnectedForge ? a.ConnectedForgeHallway - a.ReceivedForgeHallway : 0));

        decimal EffectiveElite(ActData a) =>
            _showRawForge ? a.RawForgeElite : (a.DamageElite + a.GeneratedDamageElite + (_includeConnectedForge ? a.ConnectedForgeElite - a.ReceivedForgeElite : 0));

        decimal EffectiveBoss(ActData a) =>
            _showRawForge ? a.RawForgeBoss : (a.DamageBoss + a.GeneratedDamageBoss + (_includeConnectedForge ? a.ConnectedForgeBoss - a.ReceivedForgeBoss : 0));

        var unsortedList = CardRegistry.EntityLedger.Values.OfType<RelicStats>()
            .Select(r => new { Stat = r, Agg = AggregateActData(r) })
            .Where(x => !_hideZeroDamageCards || EffectiveCombat(x.Stat) > 0 || EffectiveRun(x.Agg) > 0)
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

            "AVG_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Agg.EncountersSeenTotal > 0 ? EffectiveRun(x.Agg) / x.Agg.EncountersSeenTotal : 0)
                : unsortedList.OrderByDescending(x => x.Agg.EncountersSeenTotal > 0 ? EffectiveRun(x.Agg) / x.Agg.EncountersSeenTotal : 0),

            "HALLWAY_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveHallway(x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveHallway(x.Agg)),

            "ELITE_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveElite(x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveElite(x.Agg)),

            "BOSS_DMG" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => EffectiveBoss(x.Agg))
                : unsortedList.OrderByDescending(x => EffectiveBoss(x.Agg)),

            "ADDED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorAdded)
                : unsortedList.OrderByDescending(x => x.Stat.FloorAdded),

            "REMOVED" => _currentSort.Ascending
                ? unsortedList.OrderBy(x => x.Stat.FloorRemoved)
                : unsortedList.OrderByDescending(x => x.Stat.FloorRemoved),

            _ => unsortedList.OrderByDescending(x => EffectiveRun(x.Agg))
        };

        var finalSort = sortedList;
        if (_currentSort.Column != "RUN_DMG")
        {
            finalSort = finalSort.ThenByDescending(x => EffectiveRun(x.Agg));
        }

        var relicList = finalSort.ThenBy(x => x.Stat.FloorAdded).ToList();

        foreach (var item in relicList)
        {
            var stat = item.Stat;
            var agg = item.Agg;
            HBoxContainer row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };

            Label nameLabel = new Label { Text = GetEntityDisplayTitle(stat), CustomMinimumSize = new Vector2(300, 0) };

            decimal valCombat = EffectiveCombat(stat);
            decimal valTotal = EffectiveRun(agg);
            decimal avgTotal = agg.EncountersSeenTotal > 0 ? valTotal / agg.EncountersSeenTotal : 0;

            decimal valHallway = EffectiveHallway(agg);
            decimal avgHallway = agg.EncountersSeenHallway > 0 ? valHallway / agg.EncountersSeenHallway : 0;

            decimal valElite = EffectiveElite(agg);
            decimal avgElite = agg.EncountersSeenElite > 0 ? valElite / agg.EncountersSeenElite : 0;

            decimal valBoss = EffectiveBoss(agg);
            decimal avgBoss = agg.EncountersSeenBoss > 0 ? valBoss / agg.EncountersSeenBoss : 0;

            Color statColor = new Color("A0A8B4");

            Label runDataLabel = new Label { Text = $"{valTotal:0.##}", CustomMinimumSize = new Vector2(150, 0) };
            runDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && agg.ConnectedForgeTotal > 0 ? new Color("38BDF8") : (_showRawForge ? statColor : new Color("4ADE80")));

            Label combatDataLabel = new Label { Text = $"{valCombat:0.##}", CustomMinimumSize = new Vector2(150, 0) };
            combatDataLabel.AddThemeColorOverride("font_color", !_showRawForge && _includeConnectedForge && stat.ConnectedForgeCombat > 0 ? new Color("38BDF8") : (_showRawForge ? statColor : new Color("4ADE80")));

            Label avgDataLabel = new Label { Text = $"({avgTotal:0.#}) (#{agg.EncountersSeenTotal})", CustomMinimumSize = new Vector2(130, 0) };
            avgDataLabel.AddThemeColorOverride("font_color", statColor);

            Label hallwayLabel = new Label { Text = $"{valHallway:0.##} ({avgHallway:0.#}) (#{agg.EncountersSeenHallway})", CustomMinimumSize = new Vector2(185, 0) };
            hallwayLabel.AddThemeColorOverride("font_color", statColor);

            Label eliteLabel = new Label { Text = $"{valElite:0.##} ({avgElite:0.#}) (#{agg.EncountersSeenElite})", CustomMinimumSize = new Vector2(185, 0) };
            eliteLabel.AddThemeColorOverride("font_color", statColor);

            Label bossLabel = new Label { Text = $"{valBoss:0.##} ({avgBoss:0.#}) (#{agg.EncountersSeenBoss})", CustomMinimumSize = new Vector2(185, 0) };
            bossLabel.AddThemeColorOverride("font_color", statColor);

            string addedText = stat.FloorAdded == 0 ? "GEN" : stat.FloorAdded.ToString();
            Label addedLabel = new Label { Text = addedText, CustomMinimumSize = new Vector2(80, 0) };
            addedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            string removedText = stat.FloorRemoved <= 0 ? "N/A" : stat.FloorRemoved.ToString();
            Label removedLabel = new Label { Text = removedText, CustomMinimumSize = new Vector2(90, 0) };
            removedLabel.AddThemeColorOverride("font_color", new Color("A0A8B4"));

            row.AddChild(nameLabel);
            row.AddChild(runDataLabel);
            row.AddChild(combatDataLabel);
            row.AddChild(avgDataLabel);
            row.AddChild(hallwayLabel);
            row.AddChild(eliteLabel);
            row.AddChild(bossLabel);
            row.AddChild(addedLabel);
            row.AddChild(removedLabel);

            _fullScreenRowsContainer!.AddChild(CreateHoverableRow(row, GetPlayerRowBgColor(stat.PlayerIndex)));
        }
    }
}
